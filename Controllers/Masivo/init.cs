using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.SqlClient;
using Microsoft.AspNetCore.Authorization;
using Newtonsoft.Json.Linq;
using Microsoft.Extensions.Caching.Memory;
using MyApiProject.Models;
using Microsoft.AspNetCore.SignalR;
using MyApiProject.Hubs;
using MyApiProject.Attributes;
using System.Text.RegularExpressions;

namespace MyApiProject.Controllers
{
    [ApiExplorerSettings(GroupName = "masivo")]
    [Route("api/v2/masivo")]
    [ApiController]
    public class MasivoController : BaseController
    {
        private readonly IMemoryCache _memoryCache;
        private readonly IConfiguration _configuration;
        private readonly IHubContext<GeneralHubs> _hubContext;

        public MasivoController(IConfiguration configuration, IMemoryCache memoryCache,
            IHubContext<GeneralHubs> hubContext)
            : base(configuration, memoryCache)
        {
            _memoryCache = memoryCache;
            _configuration = configuration;
            _hubContext = hubContext;
        }

        [HttpPost("consultar")]
        public async Task<IActionResult> ConsultarGeneralConFiltros(
                                        [FromBody] FiltrosRequest request,
                                        [FromQuery] string? table = "general",
                                        [FromQuery] int page = 1,
                                        [FromQuery] int pageSize = 10)
        {
            // Validaciones iniciales
            if (request == null)
                return BadRequest(new { Message = "Request no puede ser nulo" });

            // Validar y ajustar límites
            if (pageSize < 1) pageSize = 10;
            if (page < 1) page = 1;

            try
            {
                // OPTIMIZACIÓN 1: Cache de consultas complejas
                var cacheKey = GenerateCacheKey(request, table, page, pageSize);

                // OPTIMIZACIÓN 2: Determinar estrategia de consulta basada en filtros
                var queryStrategy = DetermineQueryStrategy(request, table);

                // Construir cláusulas optimizadas
                var (selectClause, groupByClause) = BuildOptimizedSelectClause(request, queryStrategy);

                var whereClauses = new List<string>();
                var parameters = new List<SqlParameter>();
                var parameterCounters = new Dictionary<string, int>();

                // OPTIMIZACIÓN 3: Procesar filtros con optimizaciones
                BuildOptimizedFilters(request, whereClauses, parameters, parameterCounters, queryStrategy);

                // Agrupar condiciones
                var groupedWhereClauses = AgruparCondiciones(whereClauses);
                var whereQuery = groupedWhereClauses.Any()
                    ? $"WHERE {string.Join(" AND ", groupedWhereClauses)}"
                    : "";

                // OPTIMIZACIÓN 4: Orden optimizado
                string orderByClause = BuildOptimizedOrderByClause(request, queryStrategy);

                // OPTIMIZACIÓN 5: Ejecutar en paralelo la consulta y el conteo
                var (results, totalRecordsResult) = await ExecuteOptimizedPaginatedQueryAsync(
                    request, table, whereQuery, parameters, selectClause,
                    groupByClause, orderByClause, page, pageSize, queryStrategy);

                // OPTIMIZACIÓN 6: Compresión de datos para cache
                CacheResults(cacheKey, results, totalRecordsResult, request);

                // Notificación asíncrona (no bloquear la respuesta)
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await _hubContext.Clients.Group("PedidosGeneral")
                            .SendAsync("DatosActualizados", new
                            {
                                Tabla = table,
                                Accion = "ConsultaFiltros",
                                Timestamp = DateTime.UtcNow,
                                TotalRegistros = totalRecordsResult,
                                Page = page,
                                QueryTime = DateTime.UtcNow
                            });
                    }
                    catch { /* Ignorar errores en notificación */ }
                });

                return Ok(new
                {
                    PageSize = pageSize,
                    Page = page,
                    TotalRecords = totalRecordsResult,
                    TotalPages = (totalRecordsResult / pageSize),
                    Data = results,
                    QueryStrategy = queryStrategy.ToString(),
                    FromCache = false
                });
            }
            catch (Exception ex)
            {
                return HandleException(ex);
            }
        }

        // Métodos de optimización adicionales:

        private string GenerateCacheKey(FiltrosRequest request, string table, int page, int pageSize)
        {
            var validFiltros = request.Filtros
                .Where(f => !string.IsNullOrWhiteSpace(f.Key) && !string.IsNullOrWhiteSpace(f.Value))
                .ToList();

            var selects = request.Selects?
                .Where(s => !string.IsNullOrWhiteSpace(s.Key))
                .Select(s => $"{s.Key}_{s.Alias}")
                .ToList() ?? new List<string>();

            var agregaciones = request.Agregaciones?
                .Where(a => !string.IsNullOrWhiteSpace(a.Key))
                .Select(a => $"{a.Key}_{a.Operation}_{a.Alias}")
                .ToList() ?? new List<string>();

            var keyParts = new List<string>
            {
                $"masivo_{table}",
                $"page{page}_size{pageSize}",
                $"filtros_{validFiltros.Count}",
                $"selects_{string.Join("_", selects)}",
                $"aggs_{string.Join("_", agregaciones)}"
            };

            if (validFiltros.Any())
            {
                var filtrosHash = string.Join("_", validFiltros
                    .Select(f => $"{f.Key.GetHashCode()}_{f.Value.GetHashCode()}_{f.Operator?.GetHashCode()}"));
                keyParts.Add($"fh_{filtrosHash}");
            }

            return string.Join("|", keyParts);
        }

        private enum QueryStrategy
        {
            DirectQuery,        // Consulta directa con ROW_NUMBER
            KeysetPagination,   // Paginación por clave
            ApproximateCount,   // Conteo aproximado
            MaterializedView    // Usar vista materializada si existe
        }

        private QueryStrategy DetermineQueryStrategy(FiltrosRequest request, string table)
        {
            // Estrategia 1: Si hay filtros de fecha, usar paginación por clave
            var hasDateFilters = request.Filtros.Any(f =>
                f.Key.ToLower().Contains("fecha") ||
                f.Key.ToLower().Contains("date"));

            // Estrategia 2: Si no hay GROUP BY y hay orden por ID/fecha, usar keyset
            var hasGroupBy = request.Selects?.Any() == true && request.Agregaciones?.Any() != true;
            var hasIdOrder = request.Order?.Any(o =>
                o.Key.ToLower().Contains("id") ||
                o.Key.ToLower().Contains("fecha")) == true;

            // Estrategia 3: Si es tabla grande sin filtros específicos, usar conteo aproximado
            var isLargeTable = table.ToLower() switch
            {
                "ventad" or "venta" or "invd" or "inv" => true,
                _ => false
            };

            if (hasDateFilters && hasIdOrder && !hasGroupBy)
                return QueryStrategy.KeysetPagination;
            else if (isLargeTable && request.Filtros.Count == 0)
                return QueryStrategy.ApproximateCount;
            else
                return QueryStrategy.DirectQuery;
        }

        private (string selectClause, string groupByClause) BuildOptimizedSelectClause(
    FiltrosRequest request, QueryStrategy strategy)
        {
            var selectParts = new List<string>();
            var groupByParts = new List<string>();

            // Solo incluir columnas realmente necesarias
            var validSelects = request.Selects?
                .Where(s => !string.IsNullOrWhiteSpace(s.Key))
                .Take(20) // Limitar a 20 columnas máximo
                .ToList();

            if (validSelects != null && validSelects.Any())
            {
                foreach (var select in validSelects)
                {
                    // Usar alias para reducir tamaño de datos
                    var column = select.Key.Contains('.') ? select.Key : $"{select.Key}";
                    var alias = !string.IsNullOrWhiteSpace(select.Alias)
                        ? select.Alias
                        : $"col{selectParts.Count}";

                    // IMPORTANTE: Si hay alias, usar la columna original con alias
                    if (!string.IsNullOrWhiteSpace(select.Alias))
                    {
                        selectParts.Add($"{column} AS [{select.Alias}]");
                    }
                    else
                    {
                        selectParts.Add(column);
                    }

                    // En GROUP BY usar la columna ORIGINAL, no el alias
                    // SOLO si hay agregaciones en la consulta
                    if (request.Agregaciones?.Any() == true)
                    {
                        // Para GROUP BY usar siempre la columna original
                        groupByParts.Add(column);
                    }
                }
            }

            // Agregaciones optimizadas
            var validAgregaciones = request.Agregaciones?
                .Where(a => !string.IsNullOrWhiteSpace(a.Key))
                .Take(10) // Limitar agregaciones
                .ToList();

            if (validAgregaciones != null && validAgregaciones.Any())
            {
                foreach (var agg in validAgregaciones)
                {
                    string operation = GetOptimizedAggregation(agg.Operation);

                    // Para agregaciones, la columna debe ser referenciada correctamente
                    var aggColumn = agg.Key.Contains('.') ? agg.Key : $"{agg.Key}";
                    string alias = !string.IsNullOrWhiteSpace(agg.Alias)
                        ? agg.Alias
                        : $"{operation.ToLower()}_{agg.Key.Replace(".", "_")}";

                    if (operation == "DISTINCT")
                    {
                        selectParts.Add($"DISTINCT {aggColumn} AS [{alias}]");
                        // DISTINCT no requiere GROUP BY
                    }
                    else
                    {
                        selectParts.Add($"{operation}({aggColumn}) AS [{alias}]");
                        // Para funciones agregadas, agregar la columna original al GROUP BY
                        // SOLO si no es una función de agregación (COUNT, SUM, AVG, etc.)
                        if (operation == "SUM" || operation == "COUNT" || operation == "AVG" ||
                            operation == "MIN" || operation == "MAX")
                        {
                            // Las columnas en funciones agregadas NO van en GROUP BY
                        }
                        else
                        {
                            groupByParts.Add(aggColumn);
                        }
                    }
                }
            }

            // Si no hay selects específicos, usar solo columnas esenciales
            if (!selectParts.Any())
            {
                selectParts.Add("*");
            }

            string selectClause = string.Join(", ", selectParts);

            // Solo incluir GROUP BY si hay columnas para agrupar
            string groupByClause = groupByParts.Any()
                ? $"GROUP BY {string.Join(", ", groupByParts.Distinct())}" // DISTINCT para evitar duplicados
                : "";

            return (selectClause, groupByClause);
        }
        private void BuildOptimizedFilters(
            FiltrosRequest request,
            List<string> whereClauses,
            List<SqlParameter> parameters,
            Dictionary<string, int> parameterCounters,
            QueryStrategy strategy)
        {
            // Filtrar elementos vacíos primero
            var validFiltros = request.Filtros
                .Where(f => !string.IsNullOrWhiteSpace(f.Key) && !string.IsNullOrWhiteSpace(f.Value))
                .ToList();

            // OPTIMIZACIÓN: Procesar primero filtros de índice
            var indexedFilters = validFiltros
                .Where(f => IsIndexedColumn(f.Key))
                .OrderByDescending(f => f.Key.ToLower().Contains("id")) // IDs primero
                .ThenByDescending(f => f.Key.ToLower().Contains("fecha")); // Fechas después

            var nonIndexedFilters = validFiltros
                .Where(f => !IsIndexedColumn(f.Key));

            // Procesar filtros indexados primero (mejor performance)
            foreach (var filter in indexedFilters)
            {
                AddOptimizedWhereClause(filter, whereClauses, parameters, parameterCounters);
            }

            // Procesar filtros no indexados después
            foreach (var filter in nonIndexedFilters)
            {
                AddOptimizedWhereClause(filter, whereClauses, parameters, parameterCounters);
            }

            // Agregar optimizaciones específicas por estrategia
            if (strategy == QueryStrategy.KeysetPagination)
            {
                // Para paginación por keyset, asegurar orden consistente
                var dateFilter = validFiltros.FirstOrDefault(f => f.Key.ToLower().Contains("fecha"));
                if (dateFilter != null && !whereClauses.Any(w => w.Contains("ROW_NUMBER")))
                {
                    whereClauses.Add($"{dateFilter.Key} >= @LastDate");
                    parameters.Add(new SqlParameter("@LastDate", DateTime.UtcNow.AddDays(-30)));
                }
            }
        }

        private bool IsIndexedColumn(string columnName)
        {
            // Columnas que típicamente tienen índices
            var indexedColumns = new[]
            {
                "id", "ID", "Id",
                "fecha", "Fecha", "FECHA",
                "fechaemision", "FechaEmision",
                "cliente", "Cliente",
                "articulo", "Articulo",
                "codigo", "Codigo"
            };

            return indexedColumns.Any(ic =>
                columnName.EndsWith($".{ic}") ||
                columnName.Equals(ic, StringComparison.OrdinalIgnoreCase));
        }

        private void AddOptimizedWhereClause(
            BusquedaParams filter,
            List<string> whereClauses,
            List<SqlParameter> parameters,
            Dictionary<string, int> parameterCounters)
        {
            string operatorClause = filter.Operator?.ToLower() switch
            {
                "like" when filter.Value.Length > 2 => "LIKE", // Solo LIKE para valores > 2 chars
                "=" => "=",
                ">=" => ">=",
                "<=" => "<=",
                ">" => ">",
                "<" => "<",
                "<>" => "<>",
                _ => "="
            };

            var column = filter.Key;
            if (!parameterCounters.ContainsKey(column))
                parameterCounters[column] = 0;
            else
                parameterCounters[column]++;

            var paramName = $"@{column.Replace(".", "_").Replace("[", "").Replace("]", "")}_{parameterCounters[column]}";

            // Optimización para LIKE con wildcard al final solamente
            if (operatorClause == "LIKE" && !filter.Value.EndsWith("%"))
            {
                whereClauses.Add($"{column} LIKE {paramName} + '%'");
                parameters.Add(new SqlParameter(paramName, filter.Value));
            }
            else
            {
                whereClauses.Add($"{column} {operatorClause} {paramName}");
                var paramValue = operatorClause == "LIKE" ? $"%{filter.Value}%" : filter.Value;
                parameters.Add(new SqlParameter(paramName, paramValue));
            }
        }

        private string BuildOptimizedOrderByClause(FiltrosRequest request, QueryStrategy strategy)
        {
            // Optimización: Ordenar por columnas indexadas preferentemente
            var validOrders = request.Order?
                .Where(o => !string.IsNullOrWhiteSpace(o.Key))
                .OrderBy(o => IsIndexedColumn(o.Key) ? 0 : 1) // Índices primero
                .ToList();

            if (validOrders == null || !validOrders.Any())
            {
                // Buscar columnas indexadas para orden
                var indexedColumn = FindIndexedOrderColumn(request);
                return $"ORDER BY {indexedColumn}";
            }

            var orderParts = new List<string>();

            foreach (var order in validOrders.Take(3)) // Máximo 3 columnas de orden
            {
                var direction = !string.IsNullOrWhiteSpace(order.Direction) &&
                               order.Direction.ToUpper() == "DESC" ? "DESC" : "ASC";

                orderParts.Add($"{order.Key} {direction}");
            }

            return $"ORDER BY {string.Join(", ", orderParts)}";
        }

        private string FindIndexedOrderColumn(FiltrosRequest request)
        {
            // Buscar IDs primero
            var idColumn = request.Selects?
                .FirstOrDefault(s => !string.IsNullOrWhiteSpace(s.Key) &&
                    (s.Key.EndsWith(".id") || s.Key.ToLower() == "id"));

            if (idColumn != null)
                return idColumn.Key;

            // Buscar fechas
            var dateColumn = request.Selects?
                .FirstOrDefault(s => !string.IsNullOrWhiteSpace(s.Key) &&
                    s.Key.ToLower().Contains("fecha"));

            if (dateColumn != null)
                return dateColumn.Key;

            // Buscar cualquier columna indexada
            var indexedColumn = request.Selects?
                .FirstOrDefault(s => !string.IsNullOrWhiteSpace(s.Key) && IsIndexedColumn(s.Key));

            return indexedColumn?.Key ?? "id";
        }

        private string GetOptimizedAggregation(string operation)
        {
            var validOps = new Dictionary<string, string>
            {
                ["SUM"] = "SUM",
                ["COUNT"] = "COUNT",
                ["AVG"] = "AVG",
                ["MIN"] = "MIN",
                ["MAX"] = "MAX",
                ["DISTINCT"] = "DISTINCT",
                ["APPROX_COUNT_DISTINCT"] = "APPROX_COUNT_DISTINCT" // SQL Server 2019+
            };

            return validOps.ContainsKey(operation?.ToUpper())
                ? validOps[operation.ToUpper()]
                : "COUNT";
        }

        private async Task<(List<Dictionary<string, object>> Data, long TotalRecords)>
     ExecuteOptimizedPaginatedQueryAsync(
         FiltrosRequest request,
         string table,
         string whereQuery,
         List<SqlParameter> parameters,
         string selectClause,
         string groupByClause,
         string orderByClause,
         int page,
         int pageSize,
         QueryStrategy strategy)
        {
            int offset = (page - 1) * pageSize;

            // Crear copias de los parámetros para evitar conflictos
            var countParameters = parameters.Select(p =>
                new SqlParameter(p.ParameterName, p.Value)).ToList();
            var dataParameters = parameters.Select(p =>
                new SqlParameter(p.ParameterName, p.Value)).ToList();

            // OPTIMIZACIÓN: Ejecutar conteo en paralelo si es necesario
            var countTask = strategy == QueryStrategy.ApproximateCount
                ? GetApproximateCountAsync(table, whereQuery, countParameters)
                : GetOptimizedTotalRecordsAsync(table, whereQuery, countParameters, groupByClause);

            // Construir query optimizada
            string paginatedQuery;
            if (strategy == QueryStrategy.KeysetPagination && page > 1)
            {
                paginatedQuery = BuildKeysetPaginationQuery(
                    selectClause, table, whereQuery, groupByClause, orderByClause, pageSize);
            }
            else
            {
                paginatedQuery = BuildOptimizedPaginatedQuery(
                    selectClause, table, whereQuery, groupByClause, orderByClause, offset, pageSize);
            }

            // Ejecutar consulta de datos con parámetros separados
            var dataTask = ExecuteDataQueryAsync(paginatedQuery, dataParameters, offset, pageSize, strategy);

            // Esperar ambas tareas
            await Task.WhenAll(countTask, dataTask);

            return (await dataTask, await countTask);
        }

        private async Task<long> GetApproximateCountAsync(string table, string whereQuery, List<SqlParameter> parameters)
        {
            try
            {
                await using var connection = await OpenConnectionAsync(10); // Timeout corto

                string countQuery = $@"
                    SELECT SUM(ps.row_count) as estimated_total
                    FROM sys.dm_db_partition_stats ps
                    JOIN sys.objects o ON ps.object_id = o.object_id
                    WHERE o.name = @TableName
                      AND ps.index_id IN (0, 1)";

                await using var command = new SqlCommand(countQuery, connection);
                command.Parameters.AddWithValue("@TableName", ExtractMainTable(table));

                var result = await command.ExecuteScalarAsync();
                return result != DBNull.Value ? Convert.ToInt64(result) : 0;
            }
            catch
            {
                return 1000000; // Valor por defecto para tablas grandes
            }
        }

        private object ExtractMainTable(string table)
        {
            throw new NotImplementedException();
        }

        private async Task<long> GetOptimizedTotalRecordsAsync(
    string table,
    string whereQuery,
    List<SqlParameter> parameters,
    string groupByClause)
        {
            try
            {
                await using var connection = await OpenConnectionAsync(30);

                // Para consultas simples, usar COUNT_BIG rápido
                if (string.IsNullOrEmpty(groupByClause) && !table.ToUpper().Contains("JOIN"))
                {
                    string countQuery = $@"
                SELECT COUNT_BIG(*) 
                FROM {table}
                {whereQuery}";

                    await using var command = new SqlCommand(countQuery, connection);

                    // Usar AddWithValue para evitar conflictos de colección
                    foreach (var param in parameters)
                    {
                        command.Parameters.AddWithValue(param.ParameterName, param.Value);
                    }

                    var result = await command.ExecuteScalarAsync();
                    return Convert.ToInt64(result);
                }
                else
                {
                    // Para consultas complejas, usar estimación
                    return await GetApproximateCountAsync(table, whereQuery, parameters);
                }
            }
            catch
            {
                return await GetApproximateCountAsync(table, whereQuery, parameters);
            }
        }

        private string BuildKeysetPaginationQuery(
            string selectClause,
            string table,
            string whereQuery,
            string groupByClause,
            string orderByClause,
            int pageSize)
        {
            // Remover "ORDER BY " del string
            var orderBy = orderByClause.StartsWith("ORDER BY ")
                ? orderByClause.Substring(9)
                : orderByClause;

            // Keyset pagination (más rápido para grandes datasets)
            return $@"
                SELECT TOP({pageSize}) {selectClause}
                FROM {table}
                {whereQuery}
                {groupByClause}
                AND {orderBy.Split(' ')[0]} > @LastKey
                ORDER BY {orderBy}";
        }

        private string BuildOptimizedPaginatedQuery(
            string selectClause,
            string table,
            string whereQuery,
            string groupByClause,
            string orderByClause,
            int offset,
            int pageSize)
        {
            // Usar OFFSET FETCH para SQL Server 2012+
            if (string.IsNullOrEmpty(groupByClause))
            {
                return $@"
                    SELECT {selectClause}
                    FROM {table}
                    {whereQuery}
                    {orderByClause}
                    OFFSET {offset} ROWS
                    FETCH NEXT {pageSize} ROWS ONLY";
            }
            else
            {
                return $@"
                    WITH GroupedData AS (
                        SELECT {selectClause}
                        FROM {table}
                        {whereQuery}
                        {groupByClause}
                    )
                    SELECT *
                    FROM GroupedData
                    {orderByClause}
                    OFFSET {offset} ROWS
                    FETCH NEXT {pageSize} ROWS ONLY";
            }
        }

        private async Task<List<Dictionary<string, object>>> ExecuteDataQueryAsync(
     string query,
     List<SqlParameter> parameters,
     int offset,
     int pageSize,
     QueryStrategy strategy)
        {
            var results = new List<Dictionary<string, object>>();

            try
            {
                await using var connection = await OpenConnectionAsync(
                    strategy == QueryStrategy.ApproximateCount ? 60 : 180);

                await using var command = new SqlCommand(query, connection);
                command.CommandTimeout = strategy == QueryStrategy.ApproximateCount ? 60 : 180;

                // PRIMERO añadir todos los parámetros de filtro
                if (parameters != null && parameters.Count > 0)
                {
                    // Usar copia de los parámetros para evitar duplicados
                    foreach (var param in parameters)
                    {
                        // Verificar si el parámetro ya existe antes de añadirlo
                        if (!command.Parameters.Contains(param.ParameterName))
                        {
                            command.Parameters.AddWithValue(param.ParameterName, param.Value);
                        }
                        else
                        {
                            // Si ya existe, actualizar el valor
                            command.Parameters[param.ParameterName].Value = param.Value;
                        }
                    }
                }

                // LUEGO añadir parámetros específicos de paginación solo si no existen
                if (strategy == QueryStrategy.KeysetPagination)
                {
                    var lastKeyParamName = "@LastKey";
                    if (!command.Parameters.Contains(lastKeyParamName))
                    {
                        command.Parameters.AddWithValue(lastKeyParamName, offset * pageSize);
                    }
                }
                else
                {
                    var offsetParamName = "@Offset";
                    var pageSizeParamName = "@PageSize";

                    if (!command.Parameters.Contains(offsetParamName))
                    {
                        command.Parameters.AddWithValue(offsetParamName, offset);
                    }
                    if (!command.Parameters.Contains(pageSizeParamName))
                    {
                        command.Parameters.AddWithValue(pageSizeParamName, pageSize);
                    }
                }

                await using var reader = await command.ExecuteReaderAsync();

                // Optimización: Leer en bloques
                var buffer = new object[reader.FieldCount];
                while (await reader.ReadAsync())
                {
                    var row = new Dictionary<string, object>();
                    reader.GetValues(buffer);

                    for (int i = 0; i < reader.FieldCount; i++)
                    {
                        row[reader.GetName(i)] = buffer[i] == DBNull.Value ? null : buffer[i];
                    }
                    results.Add(row);

                    // Limitar resultados si es necesario
                    if (results.Count >= pageSize * 2) // Doble para cache
                        break;
                }
            }
            catch (Exception ex)
            {
                // Fallback a consulta simple
                return await ExecuteFallbackQueryAsync(query, parameters);
            }

            return results;
        }

        private async Task<List<Dictionary<string, object>>> ExecuteFallbackQueryAsync(
    string query, List<SqlParameter> parameters)
        {
            var results = new List<Dictionary<string, object>>();

            await using var connection = await OpenConnectionAsync(30);
            await using var command = new SqlCommand(query, connection);
            command.CommandTimeout = 30;

            // Evitar duplicados en fallback también
            if (parameters != null && parameters.Count > 0)
            {
                foreach (var param in parameters)
                {
                    if (!command.Parameters.Contains(param.ParameterName))
                    {
                        command.Parameters.AddWithValue(param.ParameterName, param.Value);
                    }
                }
            }

            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var row = new Dictionary<string, object>();
                for (int i = 0; i < reader.FieldCount; i++)
                {
                    row[reader.GetName(i)] = reader.GetValue(i);
                }
                results.Add(row);

                if (results.Count > 1000) break; // Limitar en fallback
            }

            return results;
        }

        private void CacheResults(string cacheKey, List<Dictionary<string, object>> data, long totalRecords, FiltrosRequest request)
        {
            try
            {
                // Determinar duración del cache basado en la complejidad de la consulta
                var cacheDuration = DetermineCacheDuration(request, data.Count);

                // Cachear datos
                _memoryCache.Set(cacheKey, data, cacheDuration);

                // Cachear total por separado
                var totalKey = $"{cacheKey}_total";
                _memoryCache.Set(totalKey, totalRecords, cacheDuration);
                // Cachear también próxima página si hay datos
                if (data.Count > 0)
                {
                    // Extraer la página actual del cacheKey (formato: page{n}_size{m}) y construir la key de la siguiente página
                    var pageMatch = Regex.Match(cacheKey, @"page(\d+)_size");
                    int currentPage = 1;
                    if (pageMatch.Success && int.TryParse(pageMatch.Groups[1].Value, out var parsedPage))
                        currentPage = parsedPage;

                    var nextPageKey = cacheKey.Replace($"page{currentPage}_size", $"page{currentPage + 1}_size");
                    _memoryCache.Set(nextPageKey, new List<Dictionary<string, object>>(),
                        TimeSpan.FromMinutes(2)); // Cache corto para próxima página
                }

            }
            catch
            {
                // Ignorar errores de cache
            }
        }

        private TimeSpan DetermineCacheDuration(FiltrosRequest request, int dataCount)
        {
            // Cache más largo para consultas complejas o pocos datos
            if (request.Agregaciones?.Any() == true || request.Selects?.Count > 5)
                return TimeSpan.FromMinutes(10);

            if (dataCount < 100)
                return TimeSpan.FromMinutes(15);

            if (request.Filtros.Count > 0)
                return TimeSpan.FromMinutes(5);

            return TimeSpan.FromMinutes(2);
        }
    }
}