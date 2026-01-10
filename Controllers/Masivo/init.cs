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

            // Validar que no haya demasiados filtros
            var totalFilters = (request.Filtros?.Count ?? 0) +
                              (request.FiltrosAnd?.Sum(g => g.Filtros?.Count ?? 0) ?? 0) +
                              (request.FiltrosOr?.Sum(g => g.Filtros?.Count ?? 0) ?? 0);



            if (page > 1000)
            {
                return BadRequest(new
                {
                    Message = "Paginación profunda no permitida",
                    Recommendation = "Use filtros o keyset pagination"
                });
            }

            var requestId = Guid.NewGuid().ToString("N")[..8];
            _logger.LogInformation("[{RequestId}] Iniciando consulta masiva - Tabla: {Table}, Página: {Page}, Tamaño: {PageSize}, Filtros: {FilterCount}, Grupos AND: {AndGroups}, Grupos OR: {OrGroups}",
                requestId, table, page, pageSize, request.Filtros?.Count ?? 0,
                request.FiltrosAnd?.Count ?? 0, request.FiltrosOr?.Count ?? 0);

            int offset = (page - 1) * pageSize;
            try
            {
                var (selectClause, groupByClause) = BuildOptimizedSelectClause(request);
                var (whereClauses, parameters) = BuildOptimizedFilters(request);
                var whereQuery = whereClauses.Any()
                    ? BuildFinalWhereClause(whereClauses)
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
                    RequestId = requestId,
                    FiltrosAplicados = totalFilters
                };

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

        #region Construcción de Queries Optimizadas

        private (string selectClause, string groupByClause) BuildOptimizedSelectClause(FiltrosRequest request)
        {
            var selectParts = new List<string>();
            var groupByParts = new List<string>();
            var hasAggregations = false;
            var hasDistinct = false;
            var distinctColumns = new List<string>();

            // 1. Procesar SELECTs normales (optimizado)
            if (request.Selects?.Any() == true)
            {
                foreach (var select in request.Selects.Where(s => !string.IsNullOrWhiteSpace(s.Key)))
                {
                    var column = FormatColumnName(select.Key);

                    if (!string.IsNullOrWhiteSpace(select.Alias) &&
                        select.Alias != select.Key.Replace(".", "_"))
                    {
                        selectParts.Add($"{column} AS [{select.Alias}]");
                    }
                    else
                    {
                        selectParts.Add(column);
                    }

                    if (!IsComplexExpression(select.Key))
                    {
                        groupByParts.Add(column);
                    }
                }
            }

            // 2. Procesar AGREGACIONES (optimizado)
            if (request.Agregaciones?.Any() == true)
            {
                // Verificar si hay agregaciones reales (COUNT, SUM, AVG, etc.)
                hasAggregations = request.Agregaciones.Any(a =>
                    !string.IsNullOrWhiteSpace(a.Operation) &&
                    a.Operation.ToUpper() != "DISTINCT");

                // Verificar si hay DISTINCT
                hasDistinct = request.Agregaciones.Any(a =>
                    a.Operation?.ToUpper() == "DISTINCT");

                foreach (var agg in request.Agregaciones.Where(a => !string.IsNullOrWhiteSpace(a.Key)))
                {
                    var operation = GetAggregationOperation(agg.Operation);
                    var columnExpression = FormatColumnExpression(agg.Key);

                    // Manejar COUNT(DISTINCT ...)
                    if (operation == "COUNT DISTINCT")
                    {
                        var distinctColumn = FormatColumnExpression(agg.Key);
                        var alias = !string.IsNullOrWhiteSpace(agg.Alias)
                            ? $"AS [{agg.Alias}]"
                            : "";
                        selectParts.Add($"COUNT(DISTINCT {distinctColumn}) {alias}");
                        hasAggregations = true; // COUNT es una agregación
                        continue;
                    }

                    // Manejar DISTINCT simple - NO es una agregación, es una cláusula SELECT
                    if (operation == "DISTINCT")
                    {
                        // Solo agregar la columna si no está ya en la lista
                        if (!selectParts.Any(p => p.Contains($"[{agg.Alias}]") ||
                            p.Contains($"{columnExpression} AS")))
                        {
                            var alias = !string.IsNullOrWhiteSpace(agg.Alias)
                                ? $"AS [{agg.Alias}]"
                                : "";
                            selectParts.Add($"{columnExpression} {alias}");

                            // Agregar a distinctColumns para GROUP BY
                            distinctColumns.Add(columnExpression);
                        }
                        continue;
                    }

                    // Otras funciones de agregación (COUNT, SUM, AVG, etc.)
                    var aggAlias = !string.IsNullOrWhiteSpace(agg.Alias)
                        ? $"AS [{agg.Alias}]"
                        : "";
                    selectParts.Add($"{operation}({columnExpression}) {aggAlias}");
                    hasAggregations = true;
                }
            }

            // 3. Si no hay SELECTs ni AGREGACIONES, usar SELECT *
            if (!selectParts.Any())
            {
                selectParts.Add("*");
            }

            // 4. Construir la cláusula SELECT
            string selectClause = string.Join(", ", selectParts);

            // 5. Aplicar DISTINCT a nivel de SELECT si hay DISTINCT
            if (hasDistinct && !hasAggregations)
            {
                // Solo aplicar DISTINCT si no hay otras agregaciones
                selectClause = $"DISTINCT {selectClause}";
            }

            // 6. GROUP BY optimizado
            string groupByClause = "";

            if (hasAggregations && groupByParts.Any())
            {
                // Si hay agregaciones, usar GROUP BY con las columnas no agregadas
                var validGroupByColumns = groupByParts
                    .Where(g => !IsComplexExpression(g))
                    .Distinct()
                    .ToList();

                if (validGroupByColumns.Any())
                {
                    groupByClause = $"GROUP BY {string.Join(", ", validGroupByColumns)}";
                }
            }
            else if (hasDistinct && !hasAggregations && distinctColumns.Any())
            {
                // Para DISTINCT sin agregaciones, crear GROUP BY con todas las columnas DISTINCT
                // Esto es equivalente a SELECT DISTINCT pero más eficiente en algunos casos
                var validDistinctColumns = distinctColumns
                    .Where(g => !IsComplexExpression(g))
                    .Distinct()
                    .ToList();

                if (validDistinctColumns.Any())
                {
                    // Si hay columnas DISTINCT específicas, agrupar por ellas
                    groupByClause = $"GROUP BY {string.Join(", ", validDistinctColumns)}";
                }
                else if (selectClause.StartsWith("DISTINCT"))
                {
                    // Si es SELECT DISTINCT *, extraer las columnas reales de la consulta
                    var actualColumns = ExtractActualColumnsFromDistinct(selectClause);
                    if (actualColumns.Any())
                    {
                        groupByClause = $"GROUP BY {string.Join(", ", actualColumns)}";
                    }
                }
            }

            return (selectClause, groupByClause);
        }

        // Método helper para extraer columnas de un SELECT DISTINCT
        private List<string> ExtractActualColumnsFromDistinct(string selectClause)
        {
            var columns = new List<string>();

            try
            {
                // Remover "DISTINCT" del inicio
                var withoutDistinct = selectClause.Trim();
                if (withoutDistinct.StartsWith("DISTINCT", StringComparison.OrdinalIgnoreCase))
                {
                    withoutDistinct = withoutDistinct.Substring(8).Trim();
                }

                // Parsear las columnas
                if (withoutDistinct == "*")
                {
                    // No podemos determinar columnas específicas para *
                    return columns;
                }

                // Dividir por comas, ignorando comas dentro de paréntesis
                int parenCount = 0;
                var currentColumn = new StringBuilder();

                foreach (char c in withoutDistinct)
                {
                    if (c == '(') parenCount++;
                    else if (c == ')') parenCount--;

                    if (c == ',' && parenCount == 0)
                    {
                        var column = currentColumn.ToString().Trim();
                        if (!string.IsNullOrEmpty(column) && !IsComplexExpression(column))
                        {
                            columns.Add(column);
                        }
                        currentColumn.Clear();
                    }
                    else
                    {
                        currentColumn.Append(c);
                    }
                }

                // Última columna
                var lastColumn = currentColumn.ToString().Trim();
                if (!string.IsNullOrEmpty(lastColumn) && !IsComplexExpression(lastColumn))
                {
                    columns.Add(lastColumn);
                }
            }
            catch
            {
                // En caso de error, retornar lista vacía
            }

            return columns;
        }

        private (List<string> whereClauses, List<SqlParameter> parameters) BuildOptimizedFilters(FiltrosRequest request)
        {
            var whereClauses = new List<string>();
            var parameters = new List<SqlParameter>();

            if (request.Filtros?.Any() != true &&
                request.FiltrosAnd?.Any() != true &&
                request.FiltrosOr?.Any() != true)
                return (whereClauses, parameters);

            int paramCounter = 0;
            int totalFilters = 0;

            // 1. Procesar filtros simples (AND implícito) - COMPATIBILIDAD CON CÓDIGO EXISTENTE
            if (request.Filtros?.Any() == true)
            {
                foreach (var filter in request.Filtros
                    .Where(f => !string.IsNullOrWhiteSpace(f.Key) &&
                           !string.IsNullOrWhiteSpace(f.Value))
                    )
                {
                    totalFilters++;
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

                        // Si el valor YA contiene wildcards, usarlo tal cual
                        // De lo contrario, agregar % al principio y final
                        string likeValue = filter.Value;

                        if (!likeValue.Contains("%") && !likeValue.Contains("_"))
                        {
                            // Solo agregar wildcards si el usuario no los especificó
                            likeValue = $"%{likeValue}%";
                        }

                        parameters.Add(new SqlParameter(paramName, likeValue));
                    }
                    else if (operatorClause == "BETWEEN")
                    {
                        HandleBetweenOperator(filter, column, whereClauses,
                            parameters, ref paramCounter);
                    }
                    else if (operatorClause == "IS NULL" || operatorClause == "IS NOT NULL")
                    {
                        whereClauses.Add($"{column} {operatorClause}");
                    }
                    else
                    {
                        var paramName = $"@p{paramCounter++}";
                        whereClauses.Add($"{column} {operatorClause} {paramName}");
                        parameters.Add(CreateTypedParameter(paramName, filter.Value));
                    }
                }
            }

            // 2. Procesar grupos AND (nueva funcionalidad)
            var andGroups = new List<string>();
            if (request.FiltrosAnd?.Any() == true)
            {
                foreach (var grupo in request.FiltrosAnd.Take(5))
                {
                    if (grupo.Filtros?.Any() != true) continue;

                    var grupoClauses = new List<string>();
                    var grupoParams = new List<SqlParameter>();
                    int grupoParamCounter = paramCounter;

                    foreach (var filter in grupo.Filtros
                        .Where(f => !string.IsNullOrWhiteSpace(f.Key) &&
                               !string.IsNullOrWhiteSpace(f.Value))
                        .Take(8))
                    {
                        totalFilters++;
                        string operatorClause = GetOperatorClause(filter.Operator);
                        string column = FormatFilterColumn(filter.Key);

                        // ... (código para procesar cada filtro)

                        // Manejar operadores especiales
                        if (operatorClause == "LIKE")
                        {
                            var paramName = $"@p{grupoParamCounter++}";
                            grupoClauses.Add($"{column} LIKE {paramName}");

                            if (filter.Value.StartsWith("%") || filter.Value.EndsWith("%"))
                            {
                                grupoParams.Add(new SqlParameter(paramName, filter.Value));
                            }
                            else
                            {
                                grupoParams.Add(new SqlParameter(paramName, $"%{filter.Value}%"));
                            }
                        }
                        else
                        {
                            var paramName = $"@p{grupoParamCounter++}";
                            grupoClauses.Add($"{column} {operatorClause} {paramName}");
                            grupoParams.Add(CreateTypedParameter(paramName, filter.Value));
                        }
                    }

                    if (grupoClauses.Any())
                    {
                        if (grupoClauses.Count > 1)
                        {
                            // Los filtros dentro del grupo se combinan según OperadorLogico
                            var operador = (grupo.OperadorLogico?.ToUpper() == "OR") ? " OR " : " AND ";
                            andGroups.Add($"({string.Join(operador, grupoClauses)})");
                        }
                        else
                        {
                            andGroups.Add(grupoClauses[0]);
                        }
                        parameters.AddRange(grupoParams);
                        paramCounter = grupoParamCounter;
                    }
                }
            }

            // 3. Procesar grupos OR - estos se combinarán con OR con los grupos AND
            var orGroups = new List<string>();
            if (request.FiltrosOr?.Any() == true)
            {
                foreach (var grupo in request.FiltrosOr.Take(5))
                {
                    if (grupo.Filtros?.Any() != true) continue;

                    var grupoClauses = new List<string>();
                    var grupoParams = new List<SqlParameter>();
                    int grupoParamCounter = paramCounter;

                    foreach (var filter in grupo.Filtros
                        .Where(f => !string.IsNullOrWhiteSpace(f.Key) &&
                               !string.IsNullOrWhiteSpace(f.Value))
                        .Take(8))
                    {
                        totalFilters++;
                        string operatorClause = GetOperatorClause(filter.Operator);
                        string column = FormatFilterColumn(filter.Key);

                        // ... (código para procesar cada filtro)

                        if (operatorClause == "LIKE")
                        {
                            var paramName = $"@p{grupoParamCounter++}";
                            grupoClauses.Add($"{column} LIKE {paramName}");

                            if (filter.Value.StartsWith("%") || filter.Value.EndsWith("%"))
                            {
                                grupoParams.Add(new SqlParameter(paramName, filter.Value));
                            }
                            else
                            {
                                grupoParams.Add(new SqlParameter(paramName, $"%{filter.Value}%"));
                            }
                        }
                        else
                        {
                            var paramName = $"@p{grupoParamCounter++}";
                            grupoClauses.Add($"{column} {operatorClause} {paramName}");
                            grupoParams.Add(CreateTypedParameter(paramName, filter.Value));
                        }
                    }

                    if (grupoClauses.Any())
                    {
                        if (grupoClauses.Count > 1)
                        {
                            var operador = (grupo.OperadorLogico?.ToUpper() == "OR") ? " OR " : " AND ";
                            orGroups.Add($"({string.Join(operador, grupoClauses)})");
                        }
                        else
                        {
                            orGroups.Add(grupoClauses[0]);
                        }
                        parameters.AddRange(grupoParams);
                        paramCounter = grupoParamCounter;
                    }
                }
            }

            // 4. Combinar todos los grupos en la cláusula WHERE final
            // Primero, combinar todos los grupos AND
            if (andGroups.Any())
            {
                if (andGroups.Count > 1)
                {
                    whereClauses.Add($"({string.Join(" AND ", andGroups)})");
                }
                else
                {
                    whereClauses.Add(andGroups[0]);
                }
            }

            // Luego, agregar los grupos OR
            // La clave está aquí: si hay grupos OR, combinarlos con OR con la condición AND existente
            if (orGroups.Any())
            {
                if (whereClauses.Any())
                {
                    // Si ya hay cláusulas AND, combinarlas con OR
                    string combinedAnd = whereClauses[0];

                    if (orGroups.Count > 1)
                    {
                        string combinedOr = $"({string.Join(" OR ", orGroups)})";
                        whereClauses[0] = $"({combinedAnd} OR {combinedOr})";
                    }
                    else
                    {
                        whereClauses[0] = $"({combinedAnd} OR {orGroups[0]})";
                    }
                }
                else
                {
                    // Si no hay cláusulas AND, usar solo las OR
                    if (orGroups.Count > 1)
                    {
                        whereClauses.Add($"({string.Join(" OR ", orGroups)})");
                    }
                    else
                    {
                        whereClauses.Add(orGroups[0]);
                    }
                }
            }

            _logger.LogDebug("Construidos {Count} filtros con {ParamCount} parámetros",
                totalFilters, parameters.Count);

            return (whereClauses, parameters);
        }

        private string BuildFinalWhereClause(List<string> whereClauses)
        {
            if (whereClauses.Count == 0)
                return "";

            if (whereClauses.Count == 1)
                return $"WHERE {whereClauses[0]}";

            var hasOrGroups = whereClauses.Any(c =>
                c.Contains(" OR ") || c.StartsWith("(") || whereClauses.Count > 1);

            var groupedClauses = whereClauses.Select(c =>
                (c.Contains(" OR ") || c.Contains(" AND ")) && !c.StartsWith("(") ? $"({c})" : c);

            return $"WHERE {string.Join(" AND ", groupedClauses)}";
        }

        private string BuildOptimizedOrderByClause(FiltrosRequest request)
        {
            if (request.Order?.Any() != true)
                return "";

            var orderParts = new List<string>();

            // Limitar a 3 columnas de orden para rendimiento
            foreach (var order in request.Order
                .Where(o => !string.IsNullOrWhiteSpace(o.Key))
                )
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
            // Verificar si el SELECT ya incluye DISTINCT
            bool hasDistinct = selectClause.TrimStart().StartsWith("DISTINCT", StringComparison.OrdinalIgnoreCase);

            // Si tiene DISTINCT, necesitamos un enfoque diferente
            if (hasDistinct)
            {
                // Extraer las columnas después del DISTINCT
                var selectWithoutDistinct = selectClause.Trim();
                if (selectWithoutDistinct.StartsWith("DISTINCT", StringComparison.OrdinalIgnoreCase))
                {
                    selectWithoutDistinct = selectWithoutDistinct.Substring(8).Trim();
                }

                // Extraer solo los nombres de los alias para la consulta externa
                var externalColumns = ExtractColumnAliases(selectWithoutDistinct);

                // Extraer los alias para el ORDER BY final
                var orderByAliases = GetOrderByAliases(selectWithoutDistinct);

                // Asegurar ORDER BY para la subconsulta interna (ROW_NUMBER)
                string innerOrderByClause;
                if (string.IsNullOrEmpty(orderByClause))
                {
                    // Usar las columnas reales, no los alias, para el ORDER BY de ROW_NUMBER
                    var firstColumn = ExtractFirstSelectColumn(selectWithoutDistinct);
                    if (!string.IsNullOrEmpty(firstColumn))
                    {
                        innerOrderByClause = $"ORDER BY {firstColumn}";
                    }
                    else
                    {
                        innerOrderByClause = "ORDER BY (SELECT NULL)";
                    }
                }
                else
                {
                    // Para consultas DISTINCT, el ORDER BY de ROW_NUMBER debe usar las columnas originales
                    innerOrderByClause = ConvertOrderByToOriginalColumns(orderByClause, selectWithoutDistinct);
                }

                // El ORDER BY final debe usar los alias de la consulta externa
                string finalOrderBy = !string.IsNullOrEmpty(orderByAliases)
                    ? $"ORDER BY {orderByAliases}"
                    : $"ORDER BY {ExtractFirstAlias(selectWithoutDistinct) ?? "row"}";

                return $@"
        SELECT DISTINCT {externalColumns}
        FROM (
            SELECT {selectWithoutDistinct},
            ROW_NUMBER() OVER ({innerOrderByClause}) AS row
            FROM {table}
            {whereQuery}
            {groupByClause}
        ) AS NumberedRows
        WHERE row > {offset} AND row <= {offset + pageSize}
        {finalOrderBy}";
            }
            else
            {
                // Código original para consultas sin DISTINCT
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
        }

        private string ConvertOrderByToOriginalColumns(string orderByClause, string selectClause)
        {
            if (string.IsNullOrEmpty(orderByClause))
                return orderByClause;

            // Extraer las partes del ORDER BY
            var orderByParts = orderByClause
                .Replace("ORDER BY", "")
                .Split(',')
                .Select(p => p.Trim())
                .ToList();

            var resultParts = new List<string>();

            foreach (var part in orderByParts)
            {
                var columnPart = part.Split(' ')[0]; // Tomar solo el nombre de la columna
                var direction = part.Contains(" DESC", StringComparison.OrdinalIgnoreCase) ? "DESC" : "ASC";

                // Buscar si esta columna es un alias en el SELECT
                var originalColumn = FindOriginalColumnForAlias(columnPart, selectClause);

                if (!string.IsNullOrEmpty(originalColumn))
                {
                    resultParts.Add($"{originalColumn} {direction}");
                }
                else
                {
                    // Si no es un alias, usar la columna tal cual
                    resultParts.Add($"{columnPart} {direction}");
                }
            }

            return $"ORDER BY {string.Join(", ", resultParts)}";
        }

        private string FindOriginalColumnForAlias(string alias, string selectClause)
        {
            if (string.IsNullOrEmpty(alias) || string.IsNullOrEmpty(selectClause))
                return null;

            // Buscar en las columnas del SELECT para encontrar el alias
            var columns = SplitSelectClause(selectClause);

            foreach (var column in columns)
            {
                var trimmedColumn = column.Trim();

                // Buscar "AS alias" o "as alias"
                if (trimmedColumn.Contains(" AS ", StringComparison.OrdinalIgnoreCase))
                {
                    var parts = trimmedColumn.Split(new[] { " AS " }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length > 1)
                    {
                        var columnAlias = parts[1].Trim().Trim('[', ']');
                        if (columnAlias.Equals(alias.Trim('[', ']'), StringComparison.OrdinalIgnoreCase))
                        {
                            return parts[0].Trim(); // Retornar la columna original
                        }
                    }
                }
            }

            return null;
        }

        private string GetOrderByAliases(string selectClause)
        {
            if (string.IsNullOrEmpty(selectClause))
                return string.Empty;

            var columns = SplitSelectClause(selectClause);
            var aliases = new List<string>();

            foreach (var column in columns)
            {
                var trimmedColumn = column.Trim();

                if (trimmedColumn.Contains(" AS ", StringComparison.OrdinalIgnoreCase))
                {
                    var parts = trimmedColumn.Split(new[] { " AS " }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length > 1)
                    {
                        var alias = parts[1].Trim();
                        if (!alias.StartsWith("[") && !alias.EndsWith("]"))
                        {
                            alias = $"[{alias}]";
                        }
                        aliases.Add(alias);
                    }
                }
                else if (trimmedColumn.Contains(" as ", StringComparison.OrdinalIgnoreCase))
                {
                    var parts = trimmedColumn.Split(new[] { " as " }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length > 1)
                    {
                        var alias = parts[1].Trim();
                        if (!alias.StartsWith("[") && !alias.EndsWith("]"))
                        {
                            alias = $"[{alias}]";
                        }
                        aliases.Add(alias);
                    }
                }
            }

            return aliases.Any() ? string.Join(", ", aliases) : string.Empty;
        }

        private string ExtractFirstAlias(string selectClause)
        {
            if (string.IsNullOrEmpty(selectClause))
                return null;

            var columns = SplitSelectClause(selectClause);

            if (!columns.Any())
                return null;

            var firstColumn = columns.First().Trim();

            if (firstColumn.Contains(" AS ", StringComparison.OrdinalIgnoreCase))
            {
                var parts = firstColumn.Split(new[] { " AS " }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length > 1)
                {
                    var alias = parts[1].Trim();
                    if (!alias.StartsWith("[") && !alias.EndsWith("]"))
                    {
                        alias = $"[{alias}]";
                    }
                    return alias;
                }
            }
            else if (firstColumn.Contains(" as ", StringComparison.OrdinalIgnoreCase))
            {
                var parts = firstColumn.Split(new[] { " as " }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length > 1)
                {
                    var alias = parts[1].Trim();
                    if (!alias.StartsWith("[") && !alias.EndsWith("]"))
                    {
                        alias = $"[{alias}]";
                    }
                    return alias;
                }
            }

            return null;
        }
        private string ExtractColumnAliases(string selectClause)
        {
            if (string.IsNullOrEmpty(selectClause))
                return "*";

            try
            {
                var columns = new List<string>();

                // Dividir por comas, pero teniendo en cuenta paréntesis
                var parts = SplitSelectClause(selectClause);

                foreach (var part in parts)
                {
                    var trimmedPart = part.Trim();

                    // Extraer el alias si existe
                    if (trimmedPart.Contains(" AS ", StringComparison.OrdinalIgnoreCase))
                    {
                        var aliasParts = trimmedPart.Split(new[] { " AS " }, StringSplitOptions.RemoveEmptyEntries);
                        if (aliasParts.Length > 1)
                        {
                            // Tomar solo el alias, asegurando que esté entre corchetes si no lo está
                            var alias = aliasParts[1].Trim();
                            if (!alias.StartsWith("[") && !alias.EndsWith("]"))
                            {
                                alias = $"[{alias}]";
                            }
                            columns.Add(alias);
                        }
                        else
                        {
                            columns.Add(trimmedPart);
                        }
                    }
                    else if (trimmedPart.Contains(" as ", StringComparison.OrdinalIgnoreCase))
                    {
                        var aliasParts = trimmedPart.Split(new[] { " as " }, StringSplitOptions.RemoveEmptyEntries);
                        if (aliasParts.Length > 1)
                        {
                            var alias = aliasParts[1].Trim();
                            if (!alias.StartsWith("[") && !alias.EndsWith("]"))
                            {
                                alias = $"[{alias}]";
                            }
                            columns.Add(alias);
                        }
                        else
                        {
                            columns.Add(trimmedPart);
                        }
                    }
                    else
                    {
                        // Si no tiene alias, usar la columna completa
                        columns.Add(trimmedPart);
                    }
                }

                return string.Join(", ", columns);
            }
            catch
            {
                // Fallback: usar la cláusula completa
                return selectClause;
            }
        }

        private List<string> SplitSelectClause(string selectClause)
        {
            var result = new List<string>();
            var currentPart = new StringBuilder();
            int parenthesisCount = 0;

            foreach (char c in selectClause)
            {
                if (c == '(') parenthesisCount++;
                else if (c == ')') parenthesisCount--;

                if (c == ',' && parenthesisCount == 0)
                {
                    result.Add(currentPart.ToString());
                    currentPart.Clear();
                }
                else
                {
                    currentPart.Append(c);
                }
            }

            if (currentPart.Length > 0)
            {
                result.Add(currentPart.ToString());
            }

            return result;
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

                // Eliminar DISTINCT si existe
                if (cleanSelect.StartsWith("DISTINCT", StringComparison.OrdinalIgnoreCase))
                {
                    cleanSelect = cleanSelect.Substring(8).Trim();
                }

                // Tomar la primera parte antes de la primera coma
                var firstColumn = cleanSelect.Split(',')[0].Trim();

                // Si la columna tiene alias, extraer solo la expresión de columna
                // Ej: [art].[Descripcion1] AS [Suggestion] → [art].[Descripcion1]
                if (firstColumn.Contains(" AS ", StringComparison.OrdinalIgnoreCase))
                {
                    var parts = firstColumn.Split(new[] { " AS " }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length > 0)
                    {
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

                // Asegurar que sea una columna válida
                if (string.IsNullOrWhiteSpace(firstColumn) || IsComplexExpression(firstColumn))
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

                // DEBUG: Log la query generada
                _logger.LogDebug("Query generada: {Query}", query);

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
                return ""; // Cambiado de "COUNT" a vacío

            var opUpper = operation.ToUpper().Trim();

            // Manejar COUNT(DISTINCT ...)
            if (opUpper.Contains("COUNT DISTINCT") || opUpper.Contains("COUNT(DISTINCT"))
                return "COUNT DISTINCT";

            // Manejar DISTINCT simple
            if (opUpper == "DISTINCT")
                return "DISTINCT"; // Esto es crucial

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

            return new SqlParameter(name, SqlDbType.NVarChar, Math.Min(value.Length, 4000)) { Value = value };
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