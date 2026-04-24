// Controllers/BaseController.cs
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Caching.Memory;
using MyApiProject.Models;
using MyApiProject.Services;
using Newtonsoft.Json;

namespace MyApiProject.Controllers
{
    public abstract class BaseController : ControllerBase
    {
        private readonly string _connectionString;
        protected readonly IMemoryCache _cache;
        protected readonly QueryBuilder QB;

        protected BaseController(IConfiguration configuration, IMemoryCache cache)
        {
            _connectionString = configuration.GetConnectionString("DefaultConnection")
                ?? throw new InvalidOperationException("Cadena de conexión 'DefaultConnection' no encontrada.");
            _cache = cache;
            QB = new QueryBuilder();
        }

        // ── Conexión ──────────────────────────────────────────────────────────

        protected async Task<SqlConnection> OpenConnectionAsync(int timeoutSeconds = 30)
        {
            var csb = new SqlConnectionStringBuilder(_connectionString)
            {
                CommandTimeout = timeoutSeconds
            };
            var connection = new SqlConnection(csb.ConnectionString);
            await connection.OpenAsync();
            return connection;
        }

        // ── Manejo de errores ─────────────────────────────────────────────────

        protected IActionResult HandleException(Exception ex, string? context = null)
        {
            if (ex is OperationCanceledException or TaskCanceledException)
                return StatusCode(499, new { Message = "Solicitud cancelada por el cliente." });

            var msg = ex.Message.Replace("\r", "").Replace("\n", " ");
            var ctx = context?.Replace("\r", "").Replace("\n", " ");

            return StatusCode(500, new { Message = $"Error: {msg}", Context = ctx });
        }

        // ── Consulta masiva paginada ───────────────────────────────────────────

        /// <summary>
        /// Ejecuta una consulta masiva paginada seleccionando automáticamente
        /// la estrategia de paginación según las características de la consulta:
        ///
        ///   Direct/CTE  → consultas simples o con offset moderado
        ///   TempTable   → GROUP BY complejo (muchas columnas o parámetros)
        ///   Keyset      → offsets grandes (&gt;= 20000 filas) con ORDER BY definido
        ///
        /// </summary>
        /// <param name="fromClause">
        ///   El FROM completo incluyendo JOINs, ej:
        ///   "VENTA AS INV INNER JOIN VENTAD AS INVD ON INV.ID = INVD.IDMovto"
        /// </param>
        protected async Task<IActionResult> ExecuteMassiveQueryAsync(
            FiltrosRequest request,
            string fromClause,
            ILogger logger,
            int maxCommandTimeout = 120)
        {
            var requestId = Guid.NewGuid().ToString("N")[..8];

            try
            {
                int page = Math.Max(1, request.Page);
                int pageSize = Math.Clamp(request.PageSize, 1, 1000);
                int offset = (page - 1) * pageSize;

                if (page > 1000)
                    return BadRequest(new
                    {
                        Message = "Paginación profunda no permitida.",
                        Recommendation = "Use filtros adicionales o keyset pagination.",
                        RequestId = requestId
                    });

                // ── Normalizar aliases del request contra el FROM ───────────
                // Corrige diferencias de capitalización: "CB.Codigo" → "cb.Codigo"
                // cuando el FROM define el alias como "cb" y el cliente manda "CB".
                NormalizeRequestAliases(request, fromClause);

                // ── Construir partes del query ──────────────────────────────
                var parameters = new List<SqlParameter>();

                var (selectClause, groupByClause, hasDistinct) = QB.BuildSelect(request);
                var whereClause = QB.BuildWhere(request, parameters);
                // HAVING: filtros sobre valores agregados (post GROUP BY)
                // useCteAliases=true porque usamos el patrón _Grouped → _Page
                var havingClause = QB.BuildHaving(request, parameters, useCteAliases: true);
                var orderBy = QB.BuildOrderBy(request);

                // ORDER BY obligatorio para ROW_NUMBER — usar fallback si no se especificó
                var orderByExpression = ResolveOrderByExpression(orderBy, selectClause, groupByClause);

                logger.LogInformation(
                    "[{Id}] Consulta masiva | FROM: {From} | Page: {Page}/{PageSize} | " +
                    "Params: {Params} | Strategy: {Strategy}",
                    requestId, fromClause[..Math.Min(60, fromClause.Length)],
                    page, pageSize, parameters.Count,
                    DeterminePaginationStrategy(groupByClause, offset, parameters.Count, pageSize).ToString());

                var strategy = DeterminePaginationStrategy(groupByClause, offset, parameters.Count, pageSize);

                logger.LogDebug(
                    "[{Id}] SELECT: {Select} | GROUP BY: {GroupBy} | WHERE: {Where} | ORDER BY: {OrderBy} | DISTINCT: {Distinct}",
                    requestId, selectClause, groupByClause, whereClause, orderByExpression, hasDistinct);

                // ── Caso especial: solo agregaciones sin GROUP BY ────────────
                // El resultado es siempre una única fila — no necesita ROW_NUMBER
                // ni paginación. Ejecutar directo para evitar el error de alias
                // en ORDER BY (los aliases de agregaciones no son válidos en OVER).
                // CONDICIÓN CORREGIDA: sin selects planos Y con agregaciones Y sin GROUP BY
                bool isSingleRowResult = string.IsNullOrEmpty(groupByClause)
                    && !request.Selects.Any(s => !string.IsNullOrWhiteSpace(s.Key))
                    && request.Agregaciones.Any(a => !string.IsNullOrWhiteSpace(a.Key))
                    && string.IsNullOrEmpty(QB.BuildOrderBy(request)); // sin ORDER BY explícito

                if (isSingleRowResult)
                {
                    var directQuery = $"SELECT {selectClause} FROM {fromClause} {whereClause} {havingClause}";
                    var directData = await ExecuteQueryDirectAsync(directQuery, parameters, maxCommandTimeout);
                    return Ok(new
                    {
                        RequestId = requestId,
                        Page = 1,
                        PageSize = 1,
                        TotalRecords = 1L,
                        TotalPages = 1L,
                        Strategy = "Direct",
                        Data = directData
                    });
                }

                // ── Ejecutar según estrategia ───────────────────────────────
                List<Dictionary<string, object?>> data;
                long totalRecords;

                if (strategy == PaginationStrategy.TempTable)
                {
                    (data, totalRecords) = await ExecuteTempTableStrategyAsync(
                        selectClause, fromClause, whereClause, groupByClause,
                        orderByExpression, offset, pageSize, parameters, maxCommandTimeout, hasDistinct, havingClause);
                }
                else if (strategy == PaginationStrategy.Keyset)
                {
                    // Keyset: lanzamos el conteo en paralelo con la query de datos
                    var countTask = GetTotalCountAsync(
                        QB.BuildCountQuery(fromClause, whereClause, groupByClause, havingClause),
                        parameters, maxCommandTimeout);

                    var dataTask = ExecuteKeysetStrategyAsync(
                        selectClause, fromClause, whereClause, groupByClause,
                        orderByExpression, offset, pageSize, parameters, maxCommandTimeout, hasDistinct, havingClause);

                    await Task.WhenAll(countTask, dataTask);
                    totalRecords = countTask.Result;
                    data = dataTask.Result;
                }
                else
                {
                    // Direct / CTE: lanzamos conteo en paralelo con datos
                    var countQuery = QB.BuildCountQuery(fromClause, whereClause, groupByClause, havingClause);
                    var dataQuery = QB.BuildPaginatedQuery(
                        selectClause, fromClause, whereClause, groupByClause,
                        orderByExpression, offset, pageSize, hasDistinct, havingClause);

                    var countTask = GetTotalCountAsync(countQuery, parameters, maxCommandTimeout);
                    var dataTask = ExecuteQueryAsync(dataQuery, parameters, offset, pageSize, maxCommandTimeout);

                    await Task.WhenAll(countTask, dataTask);
                    totalRecords = countTask.Result;
                    data = dataTask.Result;
                }

                return Ok(new
                {
                    RequestId = requestId,
                    Page = page,
                    PageSize = pageSize,
                    TotalRecords = totalRecords,
                    TotalPages = (long)Math.Ceiling((double)totalRecords / pageSize),
                    Strategy = strategy.ToString(),
                    Data = data
                });
            }
            catch (SqlException sqlEx)
            {
                logger.LogError(sqlEx, "[{Id}] Error SQL en consulta masiva | FROM: {From}",
                    requestId, fromClause);
                return StatusCode(500, new
                {
                    Message = "Error en base de datos.",
                    Details = sqlEx.Message,
                    ErrorNumber = sqlEx.Number,
                    RequestId = requestId
                });
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "[{Id}] Error interno en consulta masiva", requestId);
                return HandleException(ex, requestId);
            }
        }

        // ── Estrategias de paginación ─────────────────────────────────────────

        protected enum PaginationStrategy { Direct, TempTable, Keyset }

        protected PaginationStrategy DeterminePaginationStrategy(
            string groupByClause, int offset, int paramCount, int pageSize = 10)
        {
            // Las queries de solo-agregaciones (comparación, stats) se resuelven
            // siempre como Direct antes de llegar aquí — nunca necesitan TempTable.
            // Aún así protegemos: si hay GROUP BY, Direct/CTE es siempre correcto.

            // Las queries de stats mandan pageSize=1 — nunca necesitan TempTable ni Keyset.
            // Esto también protege el caso donde isSingleRowResult no capturó la query.
            if (pageSize <= 1)
                return PaginationStrategy.Direct;

            // Keyset para offsets muy grandes sin GROUP BY
            // (con GROUP BY el CTE _Grouped → _Page es más seguro)
            if (offset >= 20000 && string.IsNullOrEmpty(groupByClause))
                return PaginationStrategy.Keyset;

            // TempTable solo para consultas SIN GROUP BY con offset moderado-alto.
            // Umbral de parámetros elevado a 20 para evitar que las queries de
            // comparación (hasta 18 params por sub-query) caigan en TempTable y
            // generen conflictos de #Tmp_ entre peticiones concurrentes.
            bool largeOffsetNoGroup = offset >= 5000 && string.IsNullOrEmpty(groupByClause);
            bool heavyQueryNoGroup = paramCount >= 20 && string.IsNullOrEmpty(groupByClause);

            if (largeOffsetNoGroup || heavyQueryNoGroup)
                return PaginationStrategy.TempTable;

            // Default: CTE con ROW_NUMBER — maneja correctamente GROUP BY,
            // HAVING, DISTINCT y cualquier combinación de agregaciones.
            return PaginationStrategy.Direct;
        }

        // ── Ejecución de queries ──────────────────────────────────────────────

        private async Task<long> GetTotalCountAsync(
            string countQuery, List<SqlParameter> parameters, int timeoutSeconds)
        {
            await using var connection = await OpenConnectionAsync(timeoutSeconds);
            await using var command = new SqlCommand(countQuery, connection);
            command.CommandTimeout = timeoutSeconds;
            QB.AddParametersTo(command, parameters);

            var result = await command.ExecuteScalarAsync();
            return result != null && result != DBNull.Value ? Convert.ToInt64(result) : 0;
        }

        private async Task<List<Dictionary<string, object?>>> ExecuteQueryDirectAsync(
            string query, List<SqlParameter> parameters, int timeoutSeconds)
        {
            await using var connection = await OpenConnectionAsync(timeoutSeconds);
            await using var command = new SqlCommand(query, connection);
            command.CommandTimeout = timeoutSeconds;
            QB.AddParametersTo(command, parameters);
            return await ReadResultsAsync(command, 1);
        }

        private async Task<List<Dictionary<string, object?>>> ExecuteQueryAsync(
            string query, List<SqlParameter> parameters,
            int offset, int pageSize, int timeoutSeconds)
        {
            await using var connection = await OpenConnectionAsync(timeoutSeconds);
            await using var command = new SqlCommand(query, connection);
            command.CommandTimeout = QB.CalculateDynamicTimeout(query, parameters.Count);
            QB.AddParametersTo(command, parameters);
            command.Parameters.AddWithValue("@_Offset", offset);
            command.Parameters.AddWithValue("@_PageSize", pageSize);

            return await ReadResultsAsync(command, pageSize);
        }

        private async Task<(List<Dictionary<string, object?>> Data, long Total)> ExecuteTempTableStrategyAsync(
            string selectClause, string fromClause, string whereClause,
            string groupByClause, string orderByExpression,
            int offset, int pageSize, List<SqlParameter> parameters, int timeoutSeconds, bool hasDistinct = false, string havingClause = "")
        {
            var tempName = $"#Tmp_{Guid.NewGuid():N}";
            int rowLimit = offset + pageSize + 1000; // un poco más para el conteo

            await using var connection = await OpenConnectionAsync(timeoutSeconds);

            // 1. Llenar tabla temporal
            var createQuery = QB.BuildTempTableQuery(
                selectClause, fromClause, whereClause, groupByClause, tempName, rowLimit, havingClause);

            await using var createCmd = new SqlCommand(createQuery, connection);
            createCmd.CommandTimeout = timeoutSeconds;
            QB.AddParametersTo(createCmd, parameters);
            await createCmd.ExecuteNonQueryAsync();

            // 2. Crear índice en la columna de orden (mejora dramáticamente el ROW_NUMBER)
            var indexCol = QB.ExtractFirstOrderColumn(orderByExpression);
            if (!string.IsNullOrWhiteSpace(indexCol) && !QB.IsComplexExpression(indexCol))
            {
                var indexSql = $"CREATE CLUSTERED INDEX IX_{tempName.Replace("#", "")} " +
                               $"ON {tempName} ({indexCol})";
                await using var idxCmd = new SqlCommand(indexSql, connection);
                try { await idxCmd.ExecuteNonQueryAsync(); } catch { /* columna puede no existir */ }
            }

            // 3. Contar filas en la temp
            var countCmd = new SqlCommand($"SELECT COUNT_BIG(*) FROM {tempName}", connection);
            var total = Convert.ToInt64(await countCmd.ExecuteScalarAsync());

            // 4. Paginar desde la temp
            var pageQuery = $@"
WITH _Page AS (
    SELECT *, ROW_NUMBER() OVER (ORDER BY {orderByExpression}) AS _RowNum
    FROM {tempName}
)
SELECT * FROM _Page
WHERE _RowNum > @_Offset AND _RowNum <= @_Offset + @_PageSize
ORDER BY _RowNum";

            await using var pageCmd = new SqlCommand(pageQuery, connection);
            pageCmd.Parameters.AddWithValue("@_Offset", offset);
            pageCmd.Parameters.AddWithValue("@_PageSize", pageSize);

            var data = await ReadResultsAsync(pageCmd, pageSize);

            // 5. Limpiar tabla temporal (la conexión cierra sola, pero es buena práctica)
            await using var dropCmd = new SqlCommand($"DROP TABLE IF EXISTS {tempName}", connection);
            await dropCmd.ExecuteNonQueryAsync();

            return (data, total);
        }

        private async Task<List<Dictionary<string, object?>>> ExecuteKeysetStrategyAsync(
            string selectClause, string fromClause, string whereClause,
            string groupByClause, string orderByExpression,
            int offset, int pageSize, List<SqlParameter> parameters, int timeoutSeconds, bool hasDistinct = false, string havingClause = "")
        {
            if (string.IsNullOrWhiteSpace(orderByExpression) || orderByExpression == "(SELECT NULL)")
            {
                // Sin ORDER BY definido, no podemos hacer keyset → fallback a CTE
                var fallbackQuery = QB.BuildPaginatedQuery(
                    selectClause, fromClause, whereClause, groupByClause,
                    "(SELECT NULL)", offset, pageSize, hasDistinct, havingClause);

                return await ExecuteQueryAsync(fallbackQuery, parameters, offset, pageSize, timeoutSeconds);
            }

            await using var anchorConn = await OpenConnectionAsync(30);

            // Obtener el valor anchor (último valor de la página anterior)
            var anchorCol = QB.ExtractFirstOrderColumn(orderByExpression);
            var anchorQuery = $@"
SELECT {anchorCol}
FROM (
    SELECT {anchorCol},
           ROW_NUMBER() OVER (ORDER BY {orderByExpression}) AS _r
    FROM {fromClause}
    {whereClause}
    {groupByClause}
) AS _Ordered
WHERE _r = @_AnchorOffset";

            await using var anchorCmd = new SqlCommand(anchorQuery, anchorConn);
            anchorCmd.CommandTimeout = 30;
            QB.AddParametersTo(anchorCmd, parameters);
            anchorCmd.Parameters.AddWithValue("@_AnchorOffset", offset);

            var anchorValue = await anchorCmd.ExecuteScalarAsync();
            if (anchorValue == null || anchorValue == DBNull.Value)
                return new List<Dictionary<string, object?>>();

            // Construir query keyset
            var direction = orderByExpression.Contains("DESC", StringComparison.OrdinalIgnoreCase) ? "<" : ">";
            var keysetQuery = QB.BuildKeysetQuery(
                selectClause, fromClause, whereClause, groupByClause,
                orderByExpression, direction, anchorCol, pageSize);

            await using var dataConn = await OpenConnectionAsync(timeoutSeconds);
            await using var dataCmd = new SqlCommand(keysetQuery, dataConn);
            dataCmd.CommandTimeout = QB.CalculateDynamicTimeout(keysetQuery, parameters.Count);
            QB.AddParametersTo(dataCmd, parameters);
            dataCmd.Parameters.AddWithValue("@_Anchor", anchorValue);

            return await ReadResultsAsync(dataCmd, pageSize);
        }

        private static async Task<List<Dictionary<string, object?>>> ReadResultsAsync(
            SqlCommand command, int expectedPageSize)
        {
            var results = new List<Dictionary<string, object?>>(expectedPageSize);

            await using var reader = await command.ExecuteReaderAsync(
                System.Data.CommandBehavior.SequentialAccess | System.Data.CommandBehavior.SingleResult);

            var fieldNames = new string[reader.FieldCount];
            for (int i = 0; i < reader.FieldCount; i++)
                fieldNames[i] = reader.GetName(i);

            while (await reader.ReadAsync())
            {
                var row = new Dictionary<string, object?>(fieldNames.Length);
                for (int i = 0; i < fieldNames.Length; i++)
                {
                    if (fieldNames[i] == "_RowNum") continue;
                    row[fieldNames[i]] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                }
                results.Add(row);
            }

            return results;
        }

        // ── Utilidades de schema ──────────────────────────────────────────────

        protected async Task<bool> TablaExisteAsync(string nombreTabla)
        {
            const string sql = @"
                SELECT COUNT(1)
                FROM INFORMATION_SCHEMA.TABLES
                WHERE TABLE_NAME = @NombreTabla";

            await using var conn = await OpenConnectionAsync();
            await using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@NombreTabla", nombreTabla);
            return Convert.ToInt32(await cmd.ExecuteScalarAsync()) > 0;
        }

        /// <summary>
        /// Valida que un identificador simple (nombre de tabla o columna) sea seguro
        /// para interpolarlo entre corchetes en un query SQL.
        ///
        /// Solo permite caracteres válidos para identificadores SQL Server:
        /// letras, dígitos y guion bajo. Opcionalmente con esquema (schema.nombre).
        ///
        /// Usar para columnas (column), nombres de tabla sin JOINs, y cualquier
        /// identificador que no requiera la validación completa de ValidateFromClauseAsync.
        /// </summary>
        protected static (bool Valid, string? Error) ValidateIdentifier(string identifier, string label = "Identificador")
        {
            if (string.IsNullOrWhiteSpace(identifier))
                return (false, $"{label} no puede estar vacío.");

            // Permitir esquema opcional: schema.nombre
            var parts = identifier.Split('.');
            if (parts.Length > 2)
                return (false, $"{label} '{identifier}' tiene un formato inválido.");

            var identifierRegex = new System.Text.RegularExpressions.Regex(@"^[a-zA-Z0-9_]+$");
            foreach (var part in parts)
            {
                if (!identifierRegex.IsMatch(part))
                    return (false, $"{label} '{identifier}' contiene caracteres no permitidos.");
            }

            return (true, null);
        }

        /// <summary>
        /// Normaliza los aliases de tabla en un FiltrosRequest para que coincidan
        /// exactamente con los aliases definidos en el FROM clause.
        ///
        /// Problema que resuelve: el FROM define "LEFT JOIN CB AS cb" (alias en minúsculas)
        /// pero el cliente manda "CB.Codigo" (alias en mayúsculas). SQL Server es
        /// case-sensitive para aliases en algunos contextos y lanza error 4104.
        ///
        /// Solución: extraer el mapa de aliases del FROM (ej: CB→cb, ART→ART)
        /// y reescribir en el request cualquier "ALIAS.columna" que use el alias
        /// con diferente capitalización.
        /// </summary>
        protected static void NormalizeRequestAliases(FiltrosRequest request, string fromClause)
        {
            // Extraer aliases: { "CB" -> "cb", "ART" -> "ART", "P" -> "P", ... }
            var aliasMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var aliasMatches = System.Text.RegularExpressions.Regex.Matches(
                fromClause,
                @"\b(\w+)\s+AS\s+(\w+)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            foreach (System.Text.RegularExpressions.Match m in aliasMatches)
                aliasMap[m.Groups[2].Value] = m.Groups[2].Value; // alias real tal como está

            if (!aliasMap.Any()) return;

            static string FixKey(string key, Dictionary<string, string> map)
            {
                if (string.IsNullOrWhiteSpace(key) || !key.Contains('.')) return key;
                var dot = key.IndexOf('.');
                var alias = key[..dot];
                var rest = key[dot..]; // incluye el punto
                return map.TryGetValue(alias, out var realAlias) ? realAlias + rest : key;
            }

            // Normalizar Selects
            foreach (var s in request.Selects)
                s.Key = FixKey(s.Key, aliasMap);

            // Normalizar Agregaciones
            foreach (var a in request.Agregaciones)
                a.Key = FixKey(a.Key, aliasMap);

            // Normalizar Filtros planos
            foreach (var f in request.Filtros)
                f.Key = FixKey(f.Key, aliasMap);

            // Normalizar grupos FiltrosAnd y FiltrosOr
            foreach (var grupo in request.FiltrosAnd.Concat(request.FiltrosOr))
                foreach (var f in grupo.Filtros)
                    f.Key = FixKey(f.Key, aliasMap);

            // Normalizar Order
            foreach (var o in request.Order)
                o.Key = FixKey(o.Key, aliasMap);

            // Normalizar Having
            foreach (var h in request.Having)
                h.Key = FixKey(h.Key, aliasMap);
        }



        /// <summary>
        /// Valida que un FROM clause sea seguro antes de interpolarlo en un query.
        ///
        /// Estrategia de dos capas:
        ///   1. Regex — extrae todos los identificadores de tabla del FROM (incluyendo JOINs)
        ///              y verifica que cada uno solo contenga caracteres válidos para
        ///              nombres SQL ([a-zA-Z0-9_] con esquema opcional schema.tabla).
        ///              Esto bloquea cualquier intento de inyección con caracteres especiales.
        ///
        ///   2. INFORMATION_SCHEMA — verifica que cada tabla extraída exista realmente
        ///              en la base de datos. Esto bloquea identificadores sintácticamente
        ///              válidos que no sean tablas reales (ej: "sys.objects--").
        ///
        /// Retorna (true, null) si el FROM es válido.
        /// Retorna (false, mensajeError) si alguna tabla falla la validación.
        /// </summary>
        protected async Task<(bool Valid, string? Error)> ValidateFromClauseAsync(string fromClause)
        {
            if (string.IsNullOrWhiteSpace(fromClause))
                return (false, "El FROM clause no puede estar vacío.");

            // ── Capa 1: Regex ─────────────────────────────────────────────────
            // Un único patrón que captura tanto "esquema.tabla" como "tabla" sola.
            // Grupo 1 (opcional): esquema   Grupo 2: nombre de tabla
            //
            // Cubre todos los tipos de JOIN: INNER, LEFT, RIGHT, FULL, CROSS
            // y la cláusula FROM inicial.
            // Patrón en tres alternativas (grupos por pares: schema+tabla):
            //   Grupos (1,2) → tabla principal al inicio del string
            //   Grupos (3,4) → tablas de JOIN tipificado (INNER/LEFT/RIGHT/FULL/CROSS)
            //   Grupos (5,6) → tablas de JOIN simple
            // El esquema es siempre opcional; la tabla siempre presente en el segundo del par.
            const string tablePattern =
                @"^\s*\[?(?:([a-zA-Z0-9_]+)\]?\.\[?)?([a-zA-Z0-9_]+)\]?" +
                @"|(?:INNER|LEFT|RIGHT|FULL|CROSS)\s+(?:OUTER\s+)?JOIN\s+\[?(?:([a-zA-Z0-9_]+)\]?\.\[?)?([a-zA-Z0-9_]+)\]?" +
                @"|\bJOIN\s+\[?(?:([a-zA-Z0-9_]+)\]?\.\[?)?([a-zA-Z0-9_]+)\]?";

            var tableMatches = System.Text.RegularExpressions.Regex.Matches(
                fromClause,
                tablePattern,
                System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Multiline);

            if (!tableMatches.Any())
                return (false, "No se pudo extraer ningún identificador de tabla del FROM clause.");

            var tableNames = new List<(string? Schema, string Table)>();

            foreach (System.Text.RegularExpressions.Match m in tableMatches)
            {
                // Recorrer los tres pares de grupos (schema, tabla)
                var groupPairs = new[] { (1, 2), (3, 4), (5, 6) };
                foreach (var (schemaGroup, tableGroup) in groupPairs)
                {
                    if (!m.Groups[tableGroup].Success || string.IsNullOrWhiteSpace(m.Groups[tableGroup].Value))
                        continue;

                    var schema = m.Groups[schemaGroup].Success && !string.IsNullOrWhiteSpace(m.Groups[schemaGroup].Value)
                        ? m.Groups[schemaGroup].Value
                        : null;
                    tableNames.Add((schema, m.Groups[tableGroup].Value));
                    break;
                }
            }

            // Verificar que los nombres extraídos solo tengan caracteres válidos
            var identifierRegex = new System.Text.RegularExpressions.Regex(@"^[a-zA-Z0-9_]+$");
            foreach (var (schema, table) in tableNames)
            {
                if (!identifierRegex.IsMatch(table))
                    return (false, $"Nombre de tabla inválido: '{table}'.");

                if (schema != null && !identifierRegex.IsMatch(schema))
                    return (false, $"Nombre de esquema inválido: '{schema}'.");
            }

            // ── Capa 2: INFORMATION_SCHEMA ────────────────────────────────────
            const string sql = @"
                SELECT COUNT(1)
                FROM INFORMATION_SCHEMA.TABLES
                WHERE TABLE_NAME = @TableName
                  AND (@Schema IS NULL OR TABLE_SCHEMA = @Schema)";

            await using var conn = await OpenConnectionAsync(10);

            foreach (var (schema, table) in tableNames)
            {
                await using var cmd = new SqlCommand(sql, conn);
                cmd.Parameters.AddWithValue("@TableName", table);
                cmd.Parameters.AddWithValue("@Schema", (object?)schema ?? DBNull.Value);

                var count = Convert.ToInt32(await cmd.ExecuteScalarAsync());
                if (count == 0)
                    return (false, $"La tabla '{(schema != null ? $"{schema}.{table}" : table)}' no existe en la base de datos.");
            }

            return (true, null);
        }

        protected async Task<List<string>> ObtenerColumnasTablaAsync(string nombreTabla)
        {
            const string sql = @"
                SELECT COLUMN_NAME
                FROM INFORMATION_SCHEMA.COLUMNS
                WHERE TABLE_NAME = @NombreTabla
                ORDER BY ORDINAL_POSITION";

            var columnas = new List<string>();
            await using var conn = await OpenConnectionAsync();
            await using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@NombreTabla", nombreTabla);

            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                columnas.Add(reader.GetString(0));

            return columnas;
        }

        protected async Task<string> GuardarArchivoAsync(IFormFile archivo, string subCarpeta = "uploads")
        {
            var folder = Path.Combine(Directory.GetCurrentDirectory(), subCarpeta);
            Directory.CreateDirectory(folder);

            var fileName = $"{Guid.NewGuid()}{Path.GetExtension(archivo.FileName)}";
            var filePath = Path.Combine(folder, fileName);

            await using var stream = new FileStream(filePath, FileMode.Create);
            await archivo.CopyToAsync(stream);
            return filePath;
        }

        // ── INSERT genérico ───────────────────────────────────────────────────

        protected async Task<IActionResult> InsertJsonToDatabaseAsync<T>(
            T data,
            string tableName,
            IFormFile? file = null,
            string fileColumn = "file",
            Dictionary<string, object>? extraColumns = null,
            Func<SqlConnection, Task<IActionResult?>>? preValidation = null)
        {
            try
            {
                string? filePath = file != null ? await GuardarArchivoAsync(file) : null;

                await using var conn = await OpenConnectionAsync();

                if (preValidation != null)
                {
                    var vr = await preValidation(conn);
                    if (vr != null) return vr;
                }

                var props = typeof(T).GetProperties()
                    .Where(p => p.GetValue(data) != null)
                    .ToList();

                var cols = new List<string>();
                var pNames = new List<string>();
                var sqlParams = new List<SqlParameter>();

                foreach (var p in props)
                {
                    cols.Add($"[{p.Name}]");
                    pNames.Add($"@{p.Name}");
                    sqlParams.Add(new SqlParameter($"@{p.Name}", p.GetValue(data) ?? DBNull.Value));
                }

                if (extraColumns != null)
                    foreach (var kv in extraColumns)
                    {
                        cols.Add($"[{kv.Key}]");
                        pNames.Add($"@{kv.Key}");
                        sqlParams.Add(new SqlParameter($"@{kv.Key}", kv.Value ?? DBNull.Value));
                    }

                if (filePath != null)
                {
                    cols.Add($"[{fileColumn}]");
                    pNames.Add("@_FilePath");
                    sqlParams.Add(new SqlParameter("@_FilePath", filePath));
                }

                var query = $@"
                    INSERT INTO [{tableName}] ({string.Join(", ", cols)})
                    OUTPUT INSERTED.ID
                    VALUES ({string.Join(", ", pNames)})";

                await using var cmd = new SqlCommand(query, conn);
                cmd.Parameters.AddRange(sqlParams.ToArray());

                var insertedId = await cmd.ExecuteScalarAsync();
                return Ok(new { Message = $"{tableName} insertado correctamente.", Id = insertedId });
            }
            catch (Exception ex)
            {
                return HandleException(ex, $"INSERT en {tableName}");
            }
        }

        // ── UPDATE genérico ───────────────────────────────────────────────────

        protected async Task<IActionResult> UpdateJsonInDatabaseAsync<T>(
            T data,
            string tableName,
            string keyColumn,
            object keyValue,
            Func<SqlConnection, Task<IActionResult?>>? preValidation = null)
        {
            try
            {
                await using var conn = await OpenConnectionAsync();

                if (preValidation != null)
                {
                    var vr = await preValidation(conn);
                    if (vr != null) return vr;
                }

                var props = typeof(T).GetProperties()
                    .Where(p => !p.Name.Equals(keyColumn, StringComparison.OrdinalIgnoreCase))
                    .Where(p => p.GetValue(data) != null)
                    .ToList();

                if (!props.Any())
                    return BadRequest(new { Message = "No se proporcionaron campos para actualizar." });

                var setClause = string.Join(", ", props.Select(p => $"[{p.Name}] = @{p.Name}"));
                var query = $"UPDATE [{tableName}] SET {setClause} WHERE [{keyColumn}] = @_KeyValue";

                await using var cmd = new SqlCommand(query, conn);
                foreach (var p in props)
                    cmd.Parameters.AddWithValue($"@{p.Name}", p.GetValue(data)!);
                cmd.Parameters.AddWithValue("@_KeyValue", keyValue);

                var affected = await cmd.ExecuteNonQueryAsync();
                return affected > 0
                    ? Ok(new { Message = "Información actualizada correctamente." })
                    : NotFound(new { Message = "Registro no encontrado." });
            }
            catch (Exception ex)
            {
                return HandleException(ex, $"UPDATE en {tableName}");
            }
        }

        // ── Helpers privados ──────────────────────────────────────────────────

        private string ResolveOrderByExpression(string orderBy, string selectClause, string groupByClause)
        {
            if (!string.IsNullOrWhiteSpace(orderBy))
            {
                return orderBy.StartsWith("ORDER BY ", StringComparison.OrdinalIgnoreCase)
                    ? orderBy[9..]
                    : orderBy;
            }

            // Fallback 1: primera columna del GROUP BY
            // IMPORTANTE: en el CTE _Grouped → _Page, las columnas calificadas
            // (alias.columna) se convierten en columnas sin prefijo dentro del CTE.
            // "ventad.Articulo" en el GROUP BY → columna "Articulo" en _Grouped.
            // Devolver solo la parte final del identificador para evitar error 4104.
            var fromGroup = QB.ExtractFirstGroupColumn(groupByClause);
            if (!string.IsNullOrEmpty(fromGroup))
                return StripTableAlias(fromGroup);

            // Fallback 2: primera columna/alias del SELECT
            var candidate = QB.ExtractFirstSelectColumnOrAlias(selectClause);

            // Alias de función de agregado sin GROUP BY → (SELECT NULL)
            // (el alias no está disponible en el mismo nivel que ROW_NUMBER)
            if (candidate.StartsWith("[") && string.IsNullOrEmpty(groupByClause))
                return "(SELECT NULL)";

            // Columna calificada en SELECT sin alias → quitar prefijo para el CTE
            if (!string.IsNullOrEmpty(groupByClause))
                return StripTableAlias(candidate);

            return candidate;
        }

        /// <summary>
        /// Quita el prefijo de alias de tabla de un identificador calificado.
        /// "ventad.Articulo" → "Articulo"
        /// "[ventad].[Articulo]" → "Articulo"
        /// "Articulo" → "Articulo"  (sin cambio)
        /// "[totalVentas]" → "[totalVentas]"  (alias puro, sin cambio)
        /// </summary>
        private static string StripTableAlias(string identifier)
        {
            if (string.IsNullOrWhiteSpace(identifier)) return identifier;

            // Ya es un alias puro entre corchetes sin punto → devolver tal cual
            if (identifier.StartsWith("[") && !identifier.Contains('.')) return identifier;

            // Contiene punto → tomar solo la parte después del último punto
            if (identifier.Contains('.'))
            {
                var parts = identifier.Split('.');
                var last = parts[^1].Trim('[', ']').Trim();
                return string.IsNullOrWhiteSpace(last) ? identifier : last;
            }

            return identifier;
        }
    }
}