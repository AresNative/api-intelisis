using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using MyApiProject.Models;
using System.Data;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace MyApiProject.Controllers
{
    [ApiExplorerSettings(GroupName = "masivo")]
    [Route("api/v2/masivo")]
    [ApiController]
    public class MasivoController : ControllerBase
    {
        private readonly IMemoryCache _memoryCache;
        private readonly IConfiguration _configuration;
        private readonly ILogger<MasivoController> _logger;
        private readonly string _connectionString;

        public MasivoController(
            IConfiguration configuration,
            IMemoryCache memoryCache,
            ILogger<MasivoController> logger)
        {
            _configuration = configuration;
            _memoryCache = memoryCache;
            _logger = logger;
            _connectionString = configuration.GetConnectionString("DefaultConnection");
        }

        [HttpPost("consultar")]
        public async Task<IActionResult> ConsultarGeneralConFiltros(
            [FromBody] FiltrosRequest request,
            [FromQuery] string? table = "general",
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 50)
        {
            // Validaciones
            if (request == null)
                return BadRequest(new { Message = "Request no puede ser nulo" });
            if (page > 1000)
            {
                return BadRequest(new
                {
                    Message = "Paginación profunda no permitida",
                    Recommendation = "Use filtros o keyset pagination"
                });
            }
            var requestId = Guid.NewGuid().ToString("N")[..8];
            _logger.LogInformation("[{RequestId}] Iniciando consulta masiva - Tabla: {Table}, Página: {Page}, Tamaño: {PageSize}",
                requestId, table, page, pageSize);

            int offset = (page - 1) * pageSize;
            try
            {
                /*  var cacheKey = GenerateCacheKey(request, table, page, pageSize); */

                /*if (_memoryCache.TryGetValue(cacheKey, out object cached))
                    return Ok(cached); */

                var (selectClause, groupByClause) = BuildOptimizedSelectClause(request);
                var (whereClauses, parameters) = BuildOptimizedFilters(request);
                var whereQuery = whereClauses.Any()
                    ? $"WHERE {string.Join(" AND ", whereClauses)}"
                    : "";

                string orderByClause = BuildOptimizedOrderByClause(request);

                // ================================
                // TOTAL: estimado + async real
                // ================================
                bool totalAsync =
                    offset >= 5000 ||
                    !string.IsNullOrEmpty(groupByClause) ||
                    parameters.Count >= 6;

                long estimatedTotal = await GetEstimatedRowCount(
                    table, whereQuery, parameters);

                long? totalRecords = null;


                if (!totalAsync)
                {
                    totalRecords = await GetOptimizedTotalRecordsAsync(
                        table, whereQuery, groupByClause, parameters, request);
                }
                else
                {
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            var real = await GetOptimizedTotalRecordsAsync(
                                table, whereQuery, groupByClause, parameters, request);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Conteo real async falló");
                        }
                    });
                }

                var strategy = DeterminePaginationStrategy(
                    groupByClause, offset, parameters.Count,
                    totalRecords ?? estimatedTotal);

                var results = strategy switch
                {
                    PaginationStrategy.Keyset =>
                        await ExecuteKeysetPaginationAsync(
                            selectClause, table, whereQuery, groupByClause,
                            orderByClause, offset, pageSize, parameters, request),

                    PaginationStrategy.TempTable =>
                        await ExecuteTempTablePaginationAsync(
                            selectClause, table, whereQuery, groupByClause,
                            orderByClause, offset, pageSize, parameters),

                    PaginationStrategy.CTE =>
                        await ExecuteCTEPaginationAsync(
                            selectClause, table, whereQuery, groupByClause,
                            orderByClause, offset, pageSize, parameters),

                    _ =>
                        await ExecuteDirectPaginationAsync(
                            selectClause, table, whereQuery, groupByClause,
                            orderByClause, offset, pageSize, parameters)
                };

                var response = new
                {
                    Page = page,
                    PageSize = pageSize,
                    TotalRecords = totalRecords,
                    TotalEstimated = estimatedTotal,
                    TotalIsEstimated = totalRecords == null,
                    Data = results,
                    Strategy = strategy.ToString(),
                    RequestId = requestId
                };

                /* if (page <= 2 && pageSize <= 100)
                    _memoryCache.Set(cacheKey, response, TimeSpan.FromMinutes(2)); */

                return Ok(response);
            }
            catch (SqlException sqlEx)
            {
                _logger.LogError(sqlEx, "[{RequestId}] Error SQL en consulta masiva", requestId);
                return StatusCode(500, new
                {
                    Message = "Error en base de datos",
                    Details = sqlEx.Message,
                    ErrorNumber = sqlEx.Number,
                    RequestId = requestId
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[{RequestId}] Error interno en consulta masiva", requestId);
                return StatusCode(500, new
                {
                    Message = "Error interno del servidor",
                    Details = ex.Message,
                    RequestId = requestId
                });
            }
        }

        #region Cache y Claves
        private string GenerateCacheKey(FiltrosRequest req, string table, int page, int size)
                   => $"masivo|{table}|p{page}|s{size}|f{req.Filtros?.Count ?? 0}|a{req.Agregaciones?.Count ?? 0}";

        #endregion

        #region Construcción de Queries Optimizadas

        private (string selectClause, string groupByClause) BuildOptimizedSelectClause(FiltrosRequest request)
        {
            var selectParts = new List<string>();
            var groupByParts = new List<string>();
            var hasAggregations = false;

            // 1. Procesar SELECTs normales (optimizado)
            if (request.Selects?.Any() == true)
            {
                foreach (var select in request.Selects.Where(s => !string.IsNullOrWhiteSpace(s.Key)))
                {
                    var column = FormatColumnName(select.Key);

                    // Usar alias solo si es diferente del nombre de columna
                    if (!string.IsNullOrWhiteSpace(select.Alias) &&
                        select.Alias != select.Key.Replace(".", "_"))
                    {
                        selectParts.Add($"{column} AS [{select.Alias}]");
                    }
                    else
                    {
                        selectParts.Add(column);
                    }

                    // **CORRECCIÓN**: TODAS las columnas normales deben ir al GROUP BY
                    // cuando hay agregaciones, excepto si son expresiones complejas
                    if (!IsComplexExpression(select.Key))
                    {
                        groupByParts.Add(column);
                    }
                }
            }

            // 2. Procesar AGREGACIONES (optimizado)
            if (request.Agregaciones?.Any() == true)
            {
                // Verificar si hay agregaciones reales (no DISTINCT simple)
                hasAggregations = request.Agregaciones.Any(a =>
                    !string.IsNullOrWhiteSpace(a.Operation) &&
                    a.Operation.ToUpper() != "DISTINCT");

                foreach (var agg in request.Agregaciones.Where(a => !string.IsNullOrWhiteSpace(a.Key)))
                {
                    var operation = GetAggregationOperation(agg.Operation);
                    var columnExpression = FormatColumnExpression(agg.Key);

                    // Manejar COUNT(DISTINCT) optimizado
                    if (operation == "COUNT DISTINCT")
                    {
                        var distinctColumn = FormatColumnExpression(agg.Key);
                        var alias = !string.IsNullOrWhiteSpace(agg.Alias)
                            ? $"AS [{agg.Alias}]"
                            : "";

                        selectParts.Add($"COUNT(DISTINCT {distinctColumn}) {alias}");
                        continue;
                    }

                    // Manejar DISTINCT simple
                    if (operation == "DISTINCT")
                    {
                        var alias = !string.IsNullOrWhiteSpace(agg.Alias)
                            ? $"AS [{agg.Alias}]"
                            : "";

                        selectParts.Add($"DISTINCT {columnExpression} {alias}");

                        // Para DISTINCT simple sin otras agregaciones, agregar al GROUP BY
                        if (!hasAggregations)
                        {
                            groupByParts.Add(columnExpression);
                        }
                        continue;
                    }

                    // Otras funciones de agregación
                    var aggAlias = !string.IsNullOrWhiteSpace(agg.Alias)
                        ? $"AS [{agg.Alias}]"
                        : "";

                    selectParts.Add($"{operation}({columnExpression}) {aggAlias}");
                }
            }

            // 3. Si no hay SELECTs ni AGREGACIONES, usar SELECT *
            if (!selectParts.Any())
            {
                selectParts.Add("*");
            }

            string selectClause = string.Join(", ", selectParts);

            // 4. GROUP BY optimizado - **CORRECCIÓN CRÍTICA**
            string groupByClause = "";

            if (hasAggregations && request.Selects?.Any() == true)
            {
                // **IMPORTANTE**: Cuando hay agregaciones, TODAS las columnas del SELECT
                // que no sean agregaciones deben estar en el GROUP BY
                // (excepto expresiones complejas que se manejan diferente)

                // Filtrar solo columnas que no sean complejas
                var validGroupByColumns = groupByParts
                    .Where(g => !IsComplexExpression(g))
                    .Distinct()
                    .ToList();

                if (validGroupByColumns.Any())
                {
                    groupByClause = $"GROUP BY {string.Join(", ", validGroupByColumns)}";
                }
                else
                {
                    // Si todas las columnas son expresiones complejas, 
                    // no usar GROUP BY (será una agregación total)
                    hasAggregations = false;
                }
            }
            else if (request.Agregaciones?.Any(a => a.Operation?.ToUpper() == "DISTINCT") == true && !hasAggregations)
            {
                // Solo DISTINCT simple sin otras agregaciones
                // GROUP BY depende de si hay columnas normales
                if (groupByParts.Any())
                {
                    var validGroupByColumns = groupByParts
                        .Where(g => !IsComplexExpression(g))
                        .Distinct()
                        .ToList();

                    if (validGroupByColumns.Any())
                    {
                        groupByClause = $"GROUP BY {string.Join(", ", validGroupByColumns)}";
                    }
                }
            }

            return (selectClause, groupByClause);
        }

        private (List<string> whereClauses, List<SqlParameter> parameters) BuildOptimizedFilters(FiltrosRequest request)
        {
            var whereClauses = new List<string>();
            var parameters = new List<SqlParameter>();

            if (request.Filtros?.Any() != true)
                return (whereClauses, parameters);

            int paramCounter = 0;
            int filterCount = 0;

            // Limitar a 10 filtros máximo para rendimiento
            foreach (var filter in request.Filtros
                .Where(f => !string.IsNullOrWhiteSpace(f.Key) &&
                       !string.IsNullOrWhiteSpace(f.Value))
                .Take(10))
            {
                filterCount++;
                string operatorClause = GetOperatorClause(filter.Operator);
                string column = FormatFilterColumn(filter.Key);

                // Manejar operadores especiales
                if (operatorClause == "IN" || operatorClause == "NOT IN")
                {
                    HandleInOperatorOptimized(filter, column, operatorClause,
                        whereClauses, parameters, ref paramCounter);
                }
                else if (operatorClause == "LIKE")
                {
                    var paramName = $"@p{paramCounter++}";
                    whereClauses.Add($"{column} LIKE {paramName}");

                    // Optimización: LIKE con índice
                    if (filter.Value.StartsWith("%") || filter.Value.EndsWith("%"))
                    {
                        parameters.Add(new SqlParameter(paramName, filter.Value));
                    }
                    else
                    {
                        parameters.Add(new SqlParameter(paramName, $"%{filter.Value}%"));
                    }
                }
                else if (operatorClause == "BETWEEN")
                {
                    HandleBetweenOperator(filter, column, whereClauses,
                        parameters, ref paramCounter);
                }
                else
                {
                    var paramName = $"@p{paramCounter++}";
                    whereClauses.Add($"{column} {operatorClause} {paramName}");
                    parameters.Add(CreateTypedParameter(paramName, filter.Value));
                }
            }

            _logger.LogDebug("Construidos {Count} filtros con {ParamCount} parámetros",
                filterCount, parameters.Count);

            return (whereClauses, parameters);
        }

        private string BuildOptimizedOrderByClause(FiltrosRequest request)
        {
            if (request.Order?.Any() != true)
                return "";

            var orderParts = new List<string>();

            // Limitar a 3 columnas de orden para rendimiento
            foreach (var order in request.Order
                .Where(o => !string.IsNullOrWhiteSpace(o.Key))
                .Take(3))
            {
                var direction = !string.IsNullOrWhiteSpace(order.Direction) &&
                               order.Direction.ToUpper() == "DESC" ? "DESC" : "ASC";

                // Buscar alias en agregaciones
                string column = FindColumnAlias(order.Key, request) ?? FormatColumnName(order.Key);

                // Evitar ORDER BY en expresiones complejas sin alias
                if (!IsComplexExpression(column) || column.Contains("AS ["))
                {
                    orderParts.Add($"{column} {direction}");
                }
            }

            return orderParts.Any() ? $"ORDER BY {string.Join(", ", orderParts)}" : "";
        }

        #endregion

        #region Estrategias de Paginación

        private enum PaginationStrategy
        {
            Direct,     // ROW_NUMBER directo
            CTE,        // CTE con ROW_NUMBER
            TempTable,  // Tabla temporal
            Keyset      // Keyset pagination
        }

        private PaginationStrategy DeterminePaginationStrategy(
             string groupByClause,
             int offset,
             int paramCount,
             long totalRecords)
        {
            bool hasGroupBy = !string.IsNullOrEmpty(groupByClause);
            bool hasComplexGroupBy = hasGroupBy && groupByClause.Split(',').Length > 2;

            bool largeOffset = offset >= 5000;
            bool extremeOffset = offset >= 20000;
            bool manyParams = paramCount >= 6;
            bool hugeTable = totalRecords >= 1_000_000;

            if (extremeOffset)
                return PaginationStrategy.Keyset;

            if (largeOffset && hugeTable)
                return PaginationStrategy.Keyset;

            if (hasComplexGroupBy)
                return PaginationStrategy.TempTable;

            if (hasGroupBy && manyParams)
                return PaginationStrategy.TempTable;

            if (largeOffset)
                return PaginationStrategy.CTE;

            return PaginationStrategy.Direct;
        }

        private async Task<List<Dictionary<string, object>>> ExecuteDirectPaginationAsync(
            string selectClause,
            string table,
            string whereQuery,
            string groupByClause,
            string orderByClause,
            int offset,
            int pageSize,
            List<SqlParameter> parameters)
        {
            _logger.LogDebug("Ejecutando paginación directa (ROW_NUMBER)");

            await using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync();

            var query = BuildDirectPaginationQuery(
                selectClause, table, whereQuery, groupByClause,
                orderByClause, offset, pageSize);

            return await ExecuteOptimizedQuery(connection, query, parameters, pageSize);
        }

        private async Task<List<Dictionary<string, object>>> ExecuteCTEPaginationAsync(
            string selectClause,
            string table,
            string whereQuery,
            string groupByClause,
            string orderByClause,
            int offset,
            int pageSize,
            List<SqlParameter> parameters)
        {
            _logger.LogDebug("Ejecutando paginación CTE");

            await using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync();

            var query = BuildCTEPaginationQuery(
                selectClause, table, whereQuery, groupByClause,
                orderByClause, offset, pageSize);

            return await ExecuteOptimizedQuery(connection, query, parameters, pageSize);
        }

        private async Task<List<Dictionary<string, object>>> ExecuteTempTablePaginationAsync(
            string selectClause,
            string table,
            string whereQuery,
            string groupByClause,
            string orderByClause,
            int offset,
            int pageSize,
            List<SqlParameter> parameters)
        {
            _logger.LogDebug("Ejecutando paginación con tabla temporal");

            string tempTableName = $"#Temp_{Guid.NewGuid():N}";

            try
            {
                await using var connection = new SqlConnection(_connectionString);
                await connection.OpenAsync();

                // 1. Crear tabla temporal con datos filtrados
                var createQuery = BuildTempTableCreationQuery(
                    selectClause, table, whereQuery, groupByClause,
                    orderByClause, tempTableName, offset + pageSize + 1000);

                await using var createCommand = new SqlCommand(createQuery, connection);
                AddParametersOptimized(createCommand, parameters);
                createCommand.CommandTimeout = 120;

                var createResult = await createCommand.ExecuteNonQueryAsync();
                _logger.LogDebug("Tabla temporal creada con {Rows} filas estimadas", createResult);

                // 2. Crear índice en tabla temporal
                var indexColumns = ExtractIndexColumns(orderByClause, groupByClause);
                if (!string.IsNullOrEmpty(indexColumns))
                {
                    var indexQuery = $"CREATE CLUSTERED INDEX IX_{tempTableName.Replace("#", "")} " +
                                   $"ON {tempTableName} ({indexColumns})";

                    await using var indexCommand = new SqlCommand(indexQuery, connection);
                    await indexCommand.ExecuteNonQueryAsync();
                }

                // 3. Consultar desde tabla temporal usando ROW_NUMBER
                var selectQuery = $@"
                    WITH NumberedRows AS (
                        SELECT *, ROW_NUMBER() OVER (ORDER BY {indexColumns ?? "(SELECT NULL)"}) AS row
                        FROM {tempTableName}
                    )
                    SELECT *
                    FROM NumberedRows
                    WHERE row > {offset} AND row <= {offset + pageSize}
                    ORDER BY row";

                return await ExecuteOptimizedQuery(connection, selectQuery, new List<SqlParameter>(), pageSize);
            }
            finally
            {
                // Limpiar tabla temporal
                await using var cleanupConnection = new SqlConnection(_connectionString);
                await cleanupConnection.OpenAsync();

                var dropQuery = $"DROP TABLE IF EXISTS {tempTableName}";
                await using var dropCommand = new SqlCommand(dropQuery, cleanupConnection);
                await dropCommand.ExecuteNonQueryAsync();
            }
        }

        private async Task<List<Dictionary<string, object>>> ExecuteKeysetPaginationAsync(
            string selectClause,
            string table,
            string whereQuery,
            string groupByClause,
            string orderByClause,
            int offset,
            int pageSize,
            List<SqlParameter> parameters,
            FiltrosRequest request)
        {
            _logger.LogDebug("Ejecutando paginación Keyset para offset grande: {Offset}", offset);

            if (string.IsNullOrEmpty(orderByClause))
            {
                // Fallback a CTE si no hay ORDER BY
                return await ExecuteCTEPaginationAsync(
                    selectClause, table, whereQuery, groupByClause,
                    orderByClause, offset, pageSize, parameters);
            }

            await using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync();

            // Obtener el valor anchor de la página anterior usando ROW_NUMBER
            var anchorValue = await GetAnchorValue(
                connection, table, whereQuery, groupByClause,
                orderByClause, offset, parameters);

            if (anchorValue == null)
            {
                // No hay más datos
                return new List<Dictionary<string, object>>();
            }

            // Construir query keyset
            var query = BuildKeysetQuery(
                selectClause, table, whereQuery, groupByClause,
                orderByClause, anchorValue, pageSize, parameters.Count);

            // Agregar parámetro anchor
            var allParameters = new List<SqlParameter>(parameters);
            allParameters.Add(new SqlParameter("@anchor", anchorValue));

            return await ExecuteOptimizedQuery(connection, query, allParameters, pageSize);
        }

        #endregion

        #region Métodos Helper Optimizados

        private async Task<object> GetAnchorValue(
     SqlConnection connection,
     string table,
     string whereQuery,
     string groupByClause,
     string orderByClause,
     int offset,
     List<SqlParameter> parameters)
        {
            // Asegurar ORDER BY
            if (string.IsNullOrEmpty(orderByClause))
            {
                var orderColumn = ExtractFirstGroupColumn(groupByClause);
                if (string.IsNullOrEmpty(orderColumn))
                {
                    // Si no hay GROUP BY, usar valor por defecto
                    return null;
                }
                orderByClause = $"ORDER BY {orderColumn}";
            }

            // Extraer primera columna de ORDER BY
            var orderColumnName = ExtractFirstOrderColumn(orderByClause);
            if (string.IsNullOrEmpty(orderColumnName))
                return null;

            var query = $@"
        SELECT {orderColumnName}
        FROM (
            SELECT {orderColumnName}, 
            ROW_NUMBER() OVER ({orderByClause}) AS row
            FROM {table}
            {whereQuery}
            {groupByClause}
        ) AS Ordered
        WHERE row = {offset}";

            await using var command = new SqlCommand(query, connection);
            AddParametersOptimized(command, parameters);
            command.CommandTimeout = 30;

            return await command.ExecuteScalarAsync();
        }

        private string BuildDirectPaginationQuery(
    string selectClause,
    string table,
    string whereQuery,
    string groupByClause,
    string orderByClause,
    int offset,
    int pageSize)
        {
            // Asegurar ORDER BY para ROW_NUMBER
            if (string.IsNullOrEmpty(orderByClause))
            {
                var orderColumn = ExtractFirstGroupColumn(groupByClause);
                if (string.IsNullOrEmpty(orderColumn))
                {
                    // Si no hay GROUP BY, buscar la primera columna del SELECT
                    orderColumn = ExtractFirstSelectColumn(selectClause) ?? "(SELECT NULL)";
                }
                orderByClause = $"ORDER BY {orderColumn}";
            }

            return $@"
        SELECT *
        FROM (
            SELECT {selectClause},
            ROW_NUMBER() OVER ({orderByClause}) AS row
            FROM {table}
            {whereQuery}
            {groupByClause}
        ) AS NumberedRows
        WHERE row > {offset} AND row <= {offset + pageSize}
        ORDER BY row";
        }

        private string BuildCTEPaginationQuery(
            string selectClause,
            string table,
            string whereQuery,
            string groupByClause,
            string orderByClause,
            int offset,
            int pageSize)
        {
            // Asegurar ORDER BY para ROW_NUMBER
            if (string.IsNullOrEmpty(orderByClause))
            {
                var orderColumn = ExtractFirstGroupColumn(groupByClause);
                if (string.IsNullOrEmpty(orderColumn))
                {
                    // Si no hay GROUP BY, buscar la primera columna del SELECT
                    orderColumn = ExtractFirstSelectColumn(selectClause) ?? "(SELECT NULL)";
                }
                orderByClause = $"ORDER BY {orderColumn}";
            }

            return $@"
        ;WITH PaginatedData AS (
            SELECT {selectClause},
            ROW_NUMBER() OVER ({orderByClause}) AS row
            FROM {table}
            {whereQuery}
            {groupByClause}
        )
        SELECT *
        FROM PaginatedData
        WHERE row > {offset} AND row <= {offset + pageSize}
        ORDER BY row";
        }

        private string ExtractFirstSelectColumn(string selectClause)
        {
            if (string.IsNullOrEmpty(selectClause))
                return null;

            try
            {
                // Eliminar SELECT inicial si existe
                var cleanSelect = selectClause.Trim();
                if (cleanSelect.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase))
                {
                    cleanSelect = cleanSelect.Substring(6).Trim();
                }

                // Tomar la primera parte antes de la primera coma
                var firstColumn = cleanSelect.Split(',')[0].Trim();

                // Extraer el nombre de columna eliminando alias
                if (firstColumn.Contains(" AS ", StringComparison.OrdinalIgnoreCase))
                {
                    var parts = firstColumn.Split(new[] { " AS " }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length > 0)
                    {
                        // Tomar la expresión antes del AS
                        firstColumn = parts[0].Trim();
                    }
                }
                else if (firstColumn.Contains(" as ", StringComparison.OrdinalIgnoreCase))
                {
                    var parts = firstColumn.Split(new[] { " as " }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length > 0)
                    {
                        firstColumn = parts[0].Trim();
                    }
                }

                // Si la columna tiene alias entre corchetes, extraer el contenido
                if (firstColumn.StartsWith("[") && firstColumn.EndsWith("]"))
                {
                    return firstColumn;
                }

                // Si es una expresión compleja, devolver null
                if (IsComplexExpression(firstColumn))
                {
                    return null;
                }

                return firstColumn;
            }
            catch
            {
                return null;
            }
        }


        private string BuildTempTableCreationQuery(
            string selectClause,
            string table,
            string whereQuery,
            string groupByClause,
            string orderByClause,
            string tempTableName,
            int rowLimit)
        {
            var orderBy = string.IsNullOrEmpty(orderByClause)
                ? "ORDER BY (SELECT NULL)"
                : orderByClause;

            return $@"
                SELECT TOP ({rowLimit}) {selectClause}
                INTO {tempTableName}
                FROM {table}
                {whereQuery}
                {groupByClause}
                {orderBy}";
        }

        private string BuildKeysetQuery(
            string selectClause,
            string table,
            string whereQuery,
            string groupByClause,
            string orderByClause,
            object anchorValue,
            int pageSize,
            int paramCount)
        {
            var orderColumn = ExtractFirstOrderColumn(orderByClause);
            var direction = orderByClause.Contains("DESC") ? "<" : ">";

            var whereClause = string.IsNullOrEmpty(whereQuery)
                ? $"WHERE {orderColumn} {direction} @anchor"
                : $"{whereQuery} AND {orderColumn} {direction} @anchor";

            return $@"
                SELECT TOP ({pageSize}) {selectClause}
                FROM {table}
                {whereClause}
                {groupByClause}
                {orderByClause}";
        }

        private async Task<List<Dictionary<string, object>>> ExecuteOptimizedQuery(
            SqlConnection connection,
            string query,
            List<SqlParameter> parameters,
            int expectedPageSize)
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            try
            {
                var results = new List<Dictionary<string, object>>();

                await using var command = new SqlCommand(query, connection);
                command.CommandTimeout = CalculateTimeout(query, parameters?.Count ?? 0);

                AddParametersOptimized(command, parameters);

#if DEBUG
                LogQueryDetails(query, parameters);
#endif

                await using var reader = await command.ExecuteReaderAsync(
                    CommandBehavior.SequentialAccess | CommandBehavior.SingleResult);

                var fieldNames = GetFieldNames(reader);

                while (await reader.ReadAsync())
                {
                    var row = new Dictionary<string, object>(fieldNames.Length);

                    for (int i = 0; i < fieldNames.Length; i++)
                    {
                        row[fieldNames[i]] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                    }

                    results.Add(row);

                    // Parar si obtenemos más del doble del tamaño esperado
                    if (results.Count >= expectedPageSize * 2)
                        break;
                }

                stopwatch.Stop();
                _logger.LogDebug("Query ejecutada en {ElapsedMs}ms. Filas: {RowCount}",
                    stopwatch.ElapsedMilliseconds, results.Count);

                return results;
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                _logger.LogError(ex, "Error ejecutando query en {ElapsedMs}ms", stopwatch.ElapsedMilliseconds);
                throw;
            }
        }

        #endregion

        #region Métodos de Formateo y Utilidad

        private string FormatColumnName(string column)
        {
            if (string.IsNullOrWhiteSpace(column))
                return column;

            // Expresiones complejas se mantienen igual
            if (IsComplexExpression(column))
                return column;

            // Columnas con punto (tabla.columna)
            if (column.Contains("."))
            {
                var parts = column.Split('.');
                return $"[{string.Join("].[", parts)}]";
            }

            // Columnas simples
            return $"[{column}]";
        }

        private string FormatFilterColumn(string column)
        {
            if (string.IsNullOrWhiteSpace(column))
                return column;

            // Para filtros, permitir expresiones
            if (column.Contains("(") || column.Contains("*") || column.Contains("/") ||
                column.Contains("+") || column.Contains("-"))
            {
                return column;
            }

            return FormatColumnName(column);
        }

        private string FormatColumnExpression(string expression)
        {
            if (string.IsNullOrWhiteSpace(expression))
                return expression;

            // Expresiones matemáticas entre paréntesis
            if (expression.Contains("*") || expression.Contains("/") ||
                expression.Contains("+") || expression.Contains("-"))
            {
                if (!expression.StartsWith("(") || !expression.EndsWith(")"))
                    return $"({expression})";
                return expression;
            }

            return FormatColumnName(expression);
        }

        private bool IsComplexExpression(string expression)
        {
            if (string.IsNullOrWhiteSpace(expression))
                return false;

            // Eliminar espacios y corchetes para análisis
            var cleanExpr = expression.Trim()
                .Replace("[", "")
                .Replace("]", "")
                .Replace("(", "")
                .Replace(")", "");

            // Expresión es compleja si:
            // 1. Contiene operadores matemáticos
            // 2. Es una función (contiene paréntesis)
            // 3. Contiene palabras clave de expresiones
            return expression.Contains("(") && expression.Contains(")") &&
                   (expression.Contains("*") || expression.Contains("/") ||
                    expression.Contains("+") || expression.Contains("-") ||
                    expression.Contains("CASE") || expression.Contains("WHEN") ||
                    expression.Contains("CONVERT") || expression.Contains("CAST"));
        }

        private string GetOperatorClause(string? operatorStr)
        {
            if (string.IsNullOrWhiteSpace(operatorStr))
                return "=";

            return operatorStr.ToUpper() switch
            {
                "LIKE" => "LIKE",
                ">=" => ">=",
                "<=" => "<=",
                ">" => ">",
                "<" => "<",
                "<>" => "<>",
                "!=" => "<>",
                "IN" => "IN",
                "NOT IN" => "NOT IN",
                "BETWEEN" => "BETWEEN",
                "NOT BETWEEN" => "NOT BETWEEN",
                "IS NULL" => "IS NULL",
                "IS NOT NULL" => "IS NOT NULL",
                _ => "="
            };
        }

        private string GetAggregationOperation(string? operation)
        {
            if (string.IsNullOrWhiteSpace(operation))
                return "COUNT";

            var opUpper = operation.ToUpper().Trim();

            if (opUpper.Contains("COUNT DISTINCT") || opUpper.Contains("COUNT(DISTINCT"))
                return "COUNT DISTINCT";

            if (opUpper == "DISTINCT")
                return "DISTINCT";

            return opUpper switch
            {
                "SUM" => "SUM",
                "COUNT" => "COUNT",
                "AVG" => "AVG",
                "MIN" => "MIN",
                "MAX" => "MAX",
                _ => opUpper
            };
        }

        private string? FindColumnAlias(string column, FiltrosRequest request)
        {
            // Buscar en agregaciones
            var aggAlias = request.Agregaciones?
                .FirstOrDefault(a => a.Key == column || a.Alias == column);

            if (aggAlias != null && !string.IsNullOrWhiteSpace(aggAlias.Alias))
                return $"[{aggAlias.Alias}]";

            // Buscar en selects
            var selectAlias = request.Selects?
                .FirstOrDefault(s => s.Key == column || s.Alias == column);

            if (selectAlias != null && !string.IsNullOrWhiteSpace(selectAlias.Alias))
                return $"[{selectAlias.Alias}]";

            return null;
        }

        private string ExtractFirstOrderColumn(string orderByClause)
        {
            if (string.IsNullOrEmpty(orderByClause))
                return "";

            return orderByClause
                .Replace("ORDER BY", "")
                .Split(',')
                .First()
                .Trim()
                .Split(' ')
                .First();
        }

        private string ExtractFirstGroupColumn(string groupByClause)
        {
            if (string.IsNullOrEmpty(groupByClause))
                return "";

            return groupByClause
                .Replace("GROUP BY", "")
                .Split(',')
                .First()
                .Trim();
        }

        private string ExtractIndexColumns(string orderByClause, string groupByClause)
        {
            var columns = new HashSet<string>();

            if (!string.IsNullOrEmpty(orderByClause))
            {
                var orderCol = ExtractFirstOrderColumn(orderByClause);
                if (!string.IsNullOrEmpty(orderCol))
                    columns.Add(orderCol);
            }

            if (!string.IsNullOrEmpty(groupByClause))
            {
                var groupCol = ExtractFirstGroupColumn(groupByClause);
                if (!string.IsNullOrEmpty(groupCol))
                    columns.Add(groupCol);
            }

            return columns.Any() ? string.Join(", ", columns) : "";
        }

        private int CalculateTimeout(string query, int paramCount)
        {
            int baseTimeout = 30;

            if (query.Contains("GROUP BY")) baseTimeout += 15;
            if (query.Contains("JOIN")) baseTimeout += 10;
            if (paramCount > 5) baseTimeout += 5;
            if (query.Contains("WITH ")) baseTimeout += 10;

            return Math.Min(baseTimeout, 120);
        }

        private string[] GetFieldNames(SqlDataReader reader)
        {
            var fieldCount = reader.FieldCount;
            var names = new string[fieldCount];

            for (int i = 0; i < fieldCount; i++)
            {
                names[i] = reader.GetName(i);
            }

            return names;
        }

        private void AddParametersOptimized(SqlCommand command, List<SqlParameter> parameters)
        {
            if (parameters == null || parameters.Count == 0)
                return;

            foreach (var param in parameters)
            {
                if (!command.Parameters.Contains(param.ParameterName))
                {
                    var sqlParam = command.Parameters.Add(
                        param.ParameterName,
                        param.SqlDbType,
                        param.Size);

                    sqlParam.Value = param.Value ?? DBNull.Value;

                    if (param.Precision > 0) sqlParam.Precision = param.Precision;
                    if (param.Scale > 0) sqlParam.Scale = param.Scale;
                }
            }
        }

        private SqlParameter CreateTypedParameter(string name, string value)
        {
            // Determinar el tipo de dato basado en el valor
            if (int.TryParse(value, out int intValue))
                return new SqlParameter(name, SqlDbType.Int) { Value = intValue };

            if (decimal.TryParse(value, out decimal decimalValue))
                return new SqlParameter(name, SqlDbType.Decimal) { Value = decimalValue };

            if (DateTime.TryParse(value, out DateTime dateValue))
                return new SqlParameter(name, SqlDbType.DateTime) { Value = dateValue };

            if (bool.TryParse(value, out bool boolValue))
                return new SqlParameter(name, SqlDbType.Bit) { Value = boolValue };

            return new SqlParameter(name, SqlDbType.NVarChar, value.Length) { Value = value };
        }

        #endregion

        #region Métodos de Filtrado Optimizados

        private void HandleInOperatorOptimized(
            BusquedaParams filter,
            string column,
            string operatorClause,
            List<string> whereClauses,
            List<SqlParameter> parameters,
            ref int paramCounter)
        {
            try
            {
                var values = filter.Value.Split(',')
                    .Select(v => v.Trim())
                    .Where(v => !string.IsNullOrEmpty(v))
                    .Distinct() // Eliminar duplicados
                    .Take(50)   // Limitar a 50 valores máximo
                    .ToArray();

                if (values.Length == 0) return;

                // Para un solo valor, usar igualdad
                if (values.Length == 1)
                {
                    var paramName = $"@p{paramCounter++}";
                    var singleOperator = operatorClause == "IN" ? "=" : "<>";
                    whereClauses.Add($"{column} {singleOperator} {paramName}");
                    parameters.Add(CreateTypedParameter(paramName, values[0]));
                    return;
                }

                // Para múltiples valores
                var paramNames = new List<string>();
                var paramValues = new List<object>();
                var paramTypes = new HashSet<SqlDbType>();

                foreach (var value in values)
                {
                    var paramName = $"@p{paramCounter++}";
                    paramNames.Add(paramName);

                    var typedParam = CreateTypedParameter(paramName, value);
                    paramValues.Add(typedParam.Value);
                    paramTypes.Add(typedParam.SqlDbType);
                }

                // Si hay múltiples tipos, convertir todo a string
                if (paramTypes.Count > 1)
                {
                    paramNames.Clear();
                    paramValues.Clear();
                    paramCounter -= values.Length;

                    for (int i = 0; i < values.Length; i++)
                    {
                        var paramName = $"@p{paramCounter++}";
                        paramNames.Add(paramName);
                        paramValues.Add(values[i]);
                    }
                }

                var inClause = $"{column} {operatorClause} ({string.Join(", ", paramNames)})";
                whereClauses.Add(inClause);

                for (int i = 0; i < paramNames.Count; i++)
                {
                    parameters.Add(new SqlParameter(paramNames[i], paramValues[i]));
                }
            }
            catch
            {
                // Fallback a igualdad simple
                var paramName = $"@p{paramCounter++}";
                whereClauses.Add($"{column} = {paramName}");
                parameters.Add(new SqlParameter(paramName, filter.Value));
            }
        }

        private void HandleBetweenOperator(
            BusquedaParams filter,
            string column,
            List<string> whereClauses,
            List<SqlParameter> parameters,
            ref int paramCounter)
        {
            var values = filter.Value.Split(new[] { " AND ", " and " }, StringSplitOptions.RemoveEmptyEntries);
            if (values.Length != 2) return;

            var fromParam = $"@p{paramCounter++}";
            var toParam = $"@p{paramCounter++}";

            whereClauses.Add($"{column} BETWEEN {fromParam} AND {toParam}");
            parameters.Add(CreateTypedParameter(fromParam, values[0].Trim()));
            parameters.Add(CreateTypedParameter(toParam, values[1].Trim()));
        }

        #endregion

        #region Métodos de Conteo Optimizados

        private async Task<long> GetOptimizedTotalRecordsAsync(
            string table,
            string whereQuery,
            string groupByClause,
            List<SqlParameter> parameters,
            FiltrosRequest request)
        {
            try
            {
                await using var connection = new SqlConnection(_connectionString);
                await connection.OpenAsync();

                string countQuery;

                if (string.IsNullOrEmpty(groupByClause))
                {
                    // Conteo directo para consultas simples
                    countQuery = $"SELECT COUNT_BIG(*) FROM {table} {whereQuery}";
                }
                else
                {
                    // Para GROUP BY, usar COUNT DISTINCT en columnas clave
                    var primaryColumns = ExtractPrimaryGroupColumns(groupByClause);

                    if (primaryColumns.Length == 1)
                    {
                        countQuery = $"SELECT COUNT_BIG(DISTINCT {primaryColumns[0]}) FROM {table} {whereQuery}";
                    }
                    else if (primaryColumns.Length > 1)
                    {
                        // Usar subquery con DISTINCT para múltiples columnas
                        countQuery = $@"
                            SELECT COUNT_BIG(*) FROM (
                                SELECT DISTINCT {string.Join(", ", primaryColumns.Take(2))}
                                FROM {table}
                                {whereQuery}
                            ) AS DistinctGroups";
                    }
                    else
                    {
                        countQuery = $"SELECT COUNT_BIG(*) FROM {table} {whereQuery}";
                    }
                }

                await using var command = new SqlCommand(countQuery, connection);
                command.CommandTimeout = 15;
                AddParametersOptimized(command, parameters);

                var result = await command.ExecuteScalarAsync();
                return result == DBNull.Value ? 0 : Convert.ToInt64(result);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error calculando total de registros, usando estimación");

                // Estimación basada en tabla
                return await GetEstimatedRowCount(table, whereQuery, parameters);
            }
        }

        private string[] ExtractPrimaryGroupColumns(string groupByClause)
        {
            if (string.IsNullOrEmpty(groupByClause))
                return Array.Empty<string>();

            return groupByClause
                .Replace("GROUP BY", "")
                .Split(',')
                .Select(c => c.Trim())
                .Where(c => !string.IsNullOrEmpty(c) && !IsComplexExpression(c))
                .Take(2)
                .ToArray();
        }

        private async Task<long> GetEstimatedRowCount(string table, string whereQuery, List<SqlParameter> parameters)
        {
            try
            {
                await using var connection = new SqlConnection(_connectionString);
                await connection.OpenAsync();

                // Para SQL Server 2014+, usar estimación rápida
                var estimateQuery = $@"
                    SELECT CONVERT(BIGINT, rows) 
                    FROM sys.partitions 
                    WHERE object_id = OBJECT_ID('{table}') 
                    AND index_id IN (0, 1)";

                if (!string.IsNullOrEmpty(whereQuery))
                {
                    // Si hay filtros, muestrear
                    estimateQuery = $@"
                        SELECT COUNT_BIG(*) * 10 
                        FROM (SELECT TOP 1000 1 FROM {table} {whereQuery}) AS Sample";
                }

                await using var command = new SqlCommand(estimateQuery, connection);
                command.CommandTimeout = 5;

                if (!string.IsNullOrEmpty(whereQuery))
                {
                    AddParametersOptimized(command, parameters);
                }

                var result = await command.ExecuteScalarAsync();
                return result == DBNull.Value ? 10000 : Math.Max(Convert.ToInt64(result), 100);
            }
            catch
            {
                return 10000;
            }
        }

        #endregion

        #region Logging y Debug

        [Conditional("DEBUG")]
        private void LogQueryDetails(string query, List<SqlParameter> parameters)
        {
            Console.WriteLine("=== QUERY EJECUTADA ===");
            Console.WriteLine(query);

            if (parameters?.Count > 0)
            {
                Console.WriteLine("=== PARÁMETROS ===");
                foreach (var param in parameters)
                {
                    Console.WriteLine($"{param.ParameterName}: {param.Value} ({param.SqlDbType})");
                }
            }

            Console.WriteLine(new string('=', 50));
        }

        #endregion
    }
}