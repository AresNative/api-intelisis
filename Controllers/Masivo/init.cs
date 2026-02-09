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
            var hasCaseWhen = false;

            // 1. Procesar SELECTs normales (optimizado)
            if (request.Selects?.Any() == true)
            {
                foreach (var select in request.Selects.Where(s => !string.IsNullOrWhiteSpace(s.Key)))
                {
                    var column = FormatColumnName(select.Key);

                    // Verificar si es una expresión CASE WHEN
                    if (select.Key.Trim().StartsWith("CASE", StringComparison.OrdinalIgnoreCase))
                    {
                        hasCaseWhen = true;
                        var caseAlias = !string.IsNullOrWhiteSpace(select.Alias) ? $"AS [{select.Alias}]" : "";
                        selectParts.Add($"{select.Key} {caseAlias}");
                        continue;
                    }

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

            // 2. Procesar AGREGACIONES con CASE WHEN
            if (request.Agregaciones?.Any() == true)
            {
                hasAggregations = request.Agregaciones.Any(a =>
                    !string.IsNullOrWhiteSpace(a.Operation) &&
                    a.Operation.ToUpper() != "DISTINCT");

                hasDistinct = request.Agregaciones.Any(a =>
                    a.Operation?.ToUpper() == "DISTINCT");

                foreach (var agg in request.Agregaciones.Where(a => !string.IsNullOrWhiteSpace(a.Key)))
                {
                    var operation = GetAggregationOperation(agg.Operation);
                    var columnExpression = FormatColumnExpression(agg.Key);

                    var aggAlias = !string.IsNullOrWhiteSpace(agg.Alias) ? $"AS [{agg.Alias}]" : "";
                    // Verificar si es una agregación con CASE WHEN
                    if (agg.Key.Trim().StartsWith("CASE", StringComparison.OrdinalIgnoreCase))
                    {
                        hasCaseWhen = true;

                        if (operation == "COUNT DISTINCT")
                        {
                            selectParts.Add($"COUNT(DISTINCT {agg.Key}) {aggAlias}");
                        }
                        else if (!string.IsNullOrEmpty(operation) && operation != "DISTINCT")
                        {
                            selectParts.Add($"{operation}({agg.Key}) {aggAlias}");
                        }
                        else
                        {
                            selectParts.Add($"{agg.Key} {aggAlias}");
                        }

                        hasAggregations = hasAggregations || (operation != "DISTINCT");
                        continue;
                    }

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
                selectClause = $"DISTINCT {selectClause}";
            }

            // 6. GROUP BY optimizado - excluir columnas con CASE WHEN
            string groupByClause = "";

            if (hasAggregations && groupByParts.Any() && !hasCaseWhen)
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
            else if (hasDistinct && !hasAggregations && distinctColumns.Any() && !hasCaseWhen)
            {
                var validDistinctColumns = distinctColumns
                    .Where(g => !IsComplexExpression(g))
                    .Distinct()
                    .ToList();

                if (validDistinctColumns.Any())
                {
                    groupByClause = $"GROUP BY {string.Join(", ", validDistinctColumns)}";
                }
                /* else if (selectClause.StartsWith("DISTINCT"))
                {
                    // Si es SELECT DISTINCT *, extraer las columnas reales de la consulta
                    var actualColumns = ExtractActualColumnsFromDistinct(selectClause);
                    if (actualColumns.Any())
                    {
                        groupByClause = $"GROUP BY {string.Join(", ", actualColumns)}";
                    }
                } */
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

            // Solo procesar FiltrosAnd (según tu JSON de ejemplo)
            // IMPORTANTE: Si usas FiltrosAnd, NO proceses también Filtros
            if (request.FiltrosAnd?.Any() == true)
            {
                var allAndClauses = new List<string>();

                foreach (var grupo in request.FiltrosAnd)
                {
                    if (grupo.Filtros?.Any() != true) continue;

                    var grupoClauses = new List<string>();
                    var grupoParams = new List<SqlParameter>();
                    int grupoParamCounter = paramCounter;

                    foreach (var filter in grupo.Filtros
                        .Where(f => !string.IsNullOrWhiteSpace(f.Key) &&
                               !string.IsNullOrWhiteSpace(f.Value))
                        )
                    {
                        totalFilters++;
                        string operatorClause = GetOperatorClause(filter.Operator);
                        string column = FormatFilterColumn(filter.Key);

                        // Manejar operador CASE_WHEN
                        if (operatorClause == "CASE_WHEN")
                        {
                            var tempWhere = new List<string>();
                            var tempParams = new List<SqlParameter>();
                            int tempParamCounter = grupoParamCounter;

                            HandleCaseWhenOperator(filter, tempWhere,
                                tempParams, ref tempParamCounter);

                            if (tempWhere.Any())
                            {
                                grupoClauses.Add(tempWhere[0]);
                                grupoParams.AddRange(tempParams);
                                grupoParamCounter = tempParamCounter;
                            }
                        }
                        // Manejar operador TIME_BETWEEN
                        else if (operatorClause == "TIME_BETWEEN")
                        {
                            var tempWhere = new List<string>();
                            var tempParams = new List<SqlParameter>();
                            int tempParamCounter = grupoParamCounter;

                            HandleTimeBetweenOperator(filter, column, tempWhere,
                                tempParams, ref tempParamCounter);

                            if (tempWhere.Any())
                            {
                                grupoClauses.Add(tempWhere[0]);
                                grupoParams.AddRange(tempParams);
                                grupoParamCounter = tempParamCounter;
                            }
                        }
                        else if (operatorClause == "IN" || operatorClause == "NOT IN")
                        {
                            var tempWhere = new List<string>();
                            var tempParams = new List<SqlParameter>();
                            int tempParamCounter = grupoParamCounter;

                            HandleInOperatorOptimized(filter, column, operatorClause,
                                tempWhere, tempParams, ref tempParamCounter);

                            if (tempWhere.Any())
                            {
                                // IMPORTANTE: Solo agregar UNA cláusula IN
                                grupoClauses.Add(tempWhere[0]);
                                grupoParams.AddRange(tempParams);
                                grupoParamCounter = tempParamCounter;
                            }
                        }
                        else if (operatorClause == "LIKE")
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
                        else if (operatorClause == "BETWEEN")
                        {
                            var tempWhere = new List<string>();
                            var tempParams = new List<SqlParameter>();
                            int tempParamCounter = grupoParamCounter;

                            // Determinar si es un filtro de tiempo basado en el nombre de la columna
                            bool isTimeColumn = filter.Key.Contains("Hora", StringComparison.OrdinalIgnoreCase) ||
                                               filter.Key.Contains("Time", StringComparison.OrdinalIgnoreCase);

                            if (isTimeColumn)
                            {
                                HandleTimeBetweenOperator(filter, column, tempWhere,
                                    tempParams, ref tempParamCounter);
                            }
                            else
                            {
                                HandleBetweenOperator(filter, column, tempWhere,
                                    tempParams, ref tempParamCounter);
                            }

                            if (tempWhere.Any())
                            {
                                grupoClauses.Add(tempWhere[0]);
                                grupoParams.AddRange(tempParams);
                                grupoParamCounter = tempParamCounter;
                            }
                        }
                        else if (operatorClause == "IS NULL" || operatorClause == "IS NOT NULL")
                        {
                            grupoClauses.Add($"{column} {operatorClause}");
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
                            allAndClauses.Add($"({string.Join(operador, grupoClauses)})");
                        }
                        else
                        {
                            allAndClauses.Add(grupoClauses[0]);
                        }
                        parameters.AddRange(grupoParams);
                        paramCounter = grupoParamCounter;
                    }
                }

                // Agregar todas las cláusulas AND juntas
                if (allAndClauses.Any())
                {
                    if (allAndClauses.Count > 1)
                    {
                        whereClauses.Add($"({string.Join(" AND ", allAndClauses)})");
                    }
                    else
                    {
                        whereClauses.Add(allAndClauses[0]);
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

            // Caso especial para filtros de tiempo (CAST a TIME)
            if (column.Contains(".Hora") || column.EndsWith("Hora", StringComparison.OrdinalIgnoreCase))
            {
                // Si la columna contiene indicadores de hora, convertir a TIME
                return FormatColumnName(column);
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
                "CASE_WHEN" => "CASE_WHEN", // Nuevo operador para CASE WHEN
                "TIME_BETWEEN" => "TIME_BETWEEN",
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
        private void HandleCaseWhenOperator(
            BusquedaParams filter,
            List<string> whereClauses,
            List<SqlParameter> parameters,
            ref int paramCounter)
        {
            try
            {
                // Parsear la expresión CASE WHEN
                // Formato esperado: "WHEN condition1 THEN value1 WHEN condition2 THEN value2 ELSE default END"
                var caseExpression = BuildCaseExpression(filter.Value, parameters, ref paramCounter);

                if (!string.IsNullOrEmpty(caseExpression))
                {
                    // Determinar si es para WHERE o para SELECT
                    if (!string.IsNullOrEmpty(filter.Key))
                    {
                        // Para filtros en WHERE
                        whereClauses.Add($"{caseExpression} = 1");
                    }
                    else
                    {
                        // Para expresiones en SELECT (se manejará en otra parte)
                        whereClauses.Add(caseExpression);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error procesando CASE WHEN para filtro: {Value}", filter.Value);

                // Fallback: crear una expresión simple
                whereClauses.Add("1 = 1");
            }
        }

        private string BuildCaseExpression(string value, List<SqlParameter> parameters, ref int paramCounter)
        {
            // Formato JSON simplificado para CASE WHEN
            // Ejemplo: 
            // {
            //   "when": [
            //     { "condition": "edad >= 18", "then": "'Adulto'" },
            //     { "condition": "edad < 18", "then": "'Menor'" }
            //   ],
            //   "else": "'Desconocido'"
            // }

            try
            {
                // Intentar parsear como JSON
                var jsonDoc = JsonDocument.Parse(value);
                var root = jsonDoc.RootElement;

                var caseBuilder = new StringBuilder("CASE");

                // Procesar condiciones WHEN
                if (root.TryGetProperty("when", out var whenArray))
                {
                    foreach (var whenItem in whenArray.EnumerateArray())
                    {
                        if (whenItem.TryGetProperty("condition", out var condition) &&
                            whenItem.TryGetProperty("then", out var thenValue))
                        {
                            // Parsear condición y reemplazar parámetros
                            var parsedCondition = ParseCondition(condition.GetString(), parameters, ref paramCounter);
                            var parsedThen = ParseValue(thenValue.GetString(), parameters, ref paramCounter);

                            caseBuilder.Append($" WHEN {parsedCondition} THEN {parsedThen}");
                        }
                    }
                }

                // Procesar ELSE
                if (root.TryGetProperty("else", out var elseValue))
                {
                    var parsedElse = ParseValue(elseValue.GetString(), parameters, ref paramCounter);
                    caseBuilder.Append($" ELSE {parsedElse}");
                }

                caseBuilder.Append(" END");

                return caseBuilder.ToString();
            }
            catch (JsonException)
            {
                // Si no es JSON, tratar como string directo
                // Formato alternativo: "WHEN columna = valor THEN 'resultado' ELSE 'otro' END"
                if (value.Trim().StartsWith("CASE", StringComparison.OrdinalIgnoreCase))
                {
                    // Ya es una expresión CASE WHEN completa
                    return value;
                }

                // Formato simplificado: "columna = valor:resultado;columna2 = valor2:resultado2;default"
                return ParseSimpleCaseExpression(value, parameters, ref paramCounter);
            }
        }
        private string ParseSimpleCaseExpression(string expression, List<SqlParameter> parameters, ref int paramCounter)
        {
            var caseBuilder = new StringBuilder("CASE");

            // Formato: "columna = valor:resultado;columna2 = valor2:resultado2;default"
            var parts = expression.Split(';', StringSplitOptions.RemoveEmptyEntries);

            for (int i = 0; i < parts.Length; i++)
            {
                var part = parts[i].Trim();

                // Si es el último y no contiene ':', es el ELSE
                if (i == parts.Length - 1 && !part.Contains(':'))
                {
                    caseBuilder.Append($" ELSE {ParseValue(part, parameters, ref paramCounter)}");
                }
                else
                {
                    var conditionParts = part.Split(':', 2);
                    if (conditionParts.Length == 2)
                    {
                        var condition = conditionParts[0].Trim();
                        var result = conditionParts[1].Trim();

                        // Parsear condición (puede contener operadores)
                        var parsedCondition = ParseCondition(condition, parameters, ref paramCounter);
                        var parsedResult = ParseValue(result, parameters, ref paramCounter);

                        caseBuilder.Append($" WHEN {parsedCondition} THEN {parsedResult}");
                    }
                }
            }

            caseBuilder.Append(" END");
            return caseBuilder.ToString();
        }

        private string ParseCondition(string condition, List<SqlParameter> parameters, ref int paramCounter)
        {
            if (string.IsNullOrEmpty(condition))
                return "1 = 1";

            // Detectar operadores en la condición
            var operators = new[] { "=", "!=", "<>", ">", "<", ">=", "<=", "LIKE", "IN", "BETWEEN" };

            foreach (var op in operators)
            {
                if (condition.Contains($" {op} "))
                {
                    var parts = condition.Split(new[] { $" {op} " }, 2, StringSplitOptions.None);
                    if (parts.Length == 2)
                    {
                        var left = parts[0].Trim();
                        var right = parts[1].Trim();

                        // Verificar si el lado derecho necesita parámetro
                        if (right.StartsWith("'") && right.EndsWith("'"))
                        {
                            // Es string literal
                            return $"{left} {op} {right}";
                        }
                        else if (int.TryParse(right, out _) || decimal.TryParse(right, out _))
                        {
                            // Es número literal
                            return $"{left} {op} {right}";
                        }
                        else if (right.Equals("NULL", StringComparison.OrdinalIgnoreCase))
                        {
                            // Es NULL
                            return $"{left} {op} NULL";
                        }
                        else
                        {
                            // Necesita parámetro
                            var paramName = $"@p{paramCounter++}";
                            parameters.Add(CreateTypedParameter(paramName, right));
                            return $"{left} {op} {paramName}";
                        }
                    }
                }
            }

            // Si no se detectó operador, asumir igualdad con parámetro
            var paramName2 = $"@p{paramCounter++}";
            parameters.Add(CreateTypedParameter(paramName2, condition));
            return $"{condition} = {paramName2}";
        }

        private string ParseValue(string value, List<SqlParameter> parameters, ref int paramCounter)
        {
            if (string.IsNullOrEmpty(value))
                return "NULL";

            value = value.Trim();

            // Verificar si es string literal
            if ((value.StartsWith("'") && value.EndsWith("'")) ||
                (value.StartsWith("\"") && value.EndsWith("\"")))
            {
                return value;
            }

            // Verificar si es número
            if (int.TryParse(value, out int intValue))
            {
                return intValue.ToString();
            }

            if (decimal.TryParse(value, out decimal decimalValue))
            {
                return decimalValue.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }

            // Verificar si es NULL
            if (value.Equals("NULL", StringComparison.OrdinalIgnoreCase))
            {
                return "NULL";
            }

            // Es un valor que necesita parámetro
            var paramName = $"@p{paramCounter++}";
            parameters.Add(CreateTypedParameter(paramName, value));
            return paramName;
        }

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

                // Para múltiples valores - CREAR PARÁMETROS SEPARADOS
                var paramNames = new List<string>();

                foreach (var value in values)
                {
                    var paramName = $"@p{paramCounter++}";
                    paramNames.Add(paramName);
                    parameters.Add(CreateTypedParameter(paramName, value));
                }

                // Construir cláusula IN con múltiples parámetros
                var inClause = $"{column} {operatorClause} ({string.Join(", ", paramNames)})";
                whereClauses.Add(inClause);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error procesando operador IN para columna {Column}, usando igualdad simple", column);

                // Fallback a igualdad simple con el primer valor
                var values = filter.Value.Split(',')
                    .Select(v => v.Trim())
                    .FirstOrDefault(v => !string.IsNullOrEmpty(v));

                if (!string.IsNullOrEmpty(values))
                {
                    var paramName = $"@p{paramCounter++}";
                    var singleOperator = operatorClause == "IN" ? "=" : "<>";
                    whereClauses.Add($"{column} {singleOperator} {paramName}");
                    parameters.Add(CreateTypedParameter(paramName, values));
                }
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

            // Determinar si es un filtro de tiempo
            bool isTimeFilter = filter.Operator?.ToUpper() == "TIME_BETWEEN" ||
                               filter.Key.Contains(".Hora") ||
                               filter.Key.EndsWith("Hora", StringComparison.OrdinalIgnoreCase);

            var fromParam = $"@p{paramCounter++}";
            var toParam = $"@p{paramCounter++}";

            if (isTimeFilter)
            {
                // Usar CAST a TIME para filtro por horas
                whereClauses.Add($"CAST({column} AS TIME) BETWEEN {fromParam} AND {toParam}");

                // Asegurar que los valores sean tiempos válidos
                parameters.Add(CreateTimeParameter(fromParam, values[0].Trim()));
                parameters.Add(CreateTimeParameter(toParam, values[1].Trim()));
            }
            else
            {
                // Between normal
                whereClauses.Add($"{column} BETWEEN {fromParam} AND {toParam}");
                parameters.Add(CreateTypedParameter(fromParam, values[0].Trim()));
                parameters.Add(CreateTypedParameter(toParam, values[1].Trim()));
            }
        }
        private void HandleTimeBetweenOperator(
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

            // Usar CAST a TIME
            whereClauses.Add($"CAST({column} AS TIME) BETWEEN {fromParam} AND {toParam}");

            parameters.Add(CreateTimeParameter(fromParam, values[0].Trim()));
            parameters.Add(CreateTimeParameter(toParam, values[1].Trim()));
        }

        private SqlParameter CreateTimeParameter(string name, string value)
        {
            // Asegurar formato de tiempo correcto
            if (TimeSpan.TryParse(value, out TimeSpan timeValue))
            {
                // Si es solo hora, convertir a TimeSpan
                return new SqlParameter(name, SqlDbType.Time)
                {
                    Value = timeValue
                };
            }
            else if (DateTime.TryParse(value, out DateTime dateTimeValue))
            {
                // Si es DateTime, extraer solo la parte de tiempo
                return new SqlParameter(name, SqlDbType.Time)
                {
                    Value = dateTimeValue.TimeOfDay
                };
            }
            else
            {
                // Valor por defecto si no se puede parsear
                return new SqlParameter(name, SqlDbType.Time)
                {
                    Value = TimeSpan.Parse("00:00:00")
                };
            }
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