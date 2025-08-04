using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Caching.Memory;
using System.Data;
using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using MyApiProject.Models;

namespace MyApiProject.Controllers
{
    public partial class Reporteria : BaseController
    {
        private readonly IMemoryCache _memoryCache;
        private readonly IHubContext<ReportHub> _hubContext;

        public Reporteria(IConfiguration configuration, IMemoryCache memoryCache, IHubContext<ReportHub> hubContext)
            : base(configuration, memoryCache)
        {
            _memoryCache = memoryCache;
            _hubContext = hubContext;
        }

        [HttpPost("api/v2/reporteria/ventas")]
        public async Task<IActionResult> ObtenerVentas(
            [FromBody] ReporteriaRequest request,
            [FromQuery] bool sum = false,
            [FromQuery] bool distinct = false,
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 10,
            [FromQuery] string connectionId = null) =>
            await GetReportData(request, sum, distinct, page, pageSize, GetVentasBaseQuery(), ReportType.Ventas, connectionId);

        [HttpPost("api/v2/reporteria/compras")]
        public async Task<IActionResult> ObtenerCompras(
            [FromBody] ReporteriaRequest request,
            [FromQuery] bool sum = false,
            [FromQuery] bool distinct = false,
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 10,
            [FromQuery] string connectionId = null) =>
            await GetReportData(request, sum, distinct, page, pageSize, GetComprasBaseQuery(), ReportType.Compras, connectionId);

        [HttpPost("api/v2/reporteria/mermas")]
        public async Task<IActionResult> ObtenerMermas(
            [FromBody] ReporteriaRequest request,
            [FromQuery] bool sum = false,
            [FromQuery] bool distinct = false,
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 10,
            [FromQuery] string connectionId = null) =>
            await GetReportData(request, sum, distinct, page, pageSize, GetMermasBaseQuery(), ReportType.Mermas, connectionId);

        [HttpPost("api/v2/reporteria/almacen")]
        public async Task<IActionResult> ObtenerAlmacen(
            [FromBody] ReporteriaRequest request,
            [FromQuery] bool sum = false,
            [FromQuery] bool distinct = false,
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 10,
            [FromQuery] string connectionId = null) =>
            await GetReportData(request, sum, distinct, page, pageSize, GetAlmacenBaseQuery(), ReportType.Almacen, connectionId);

        [HttpPost("api/v2/reporteria/utilidadbruta")]
        public async Task<IActionResult> ObtenerUtilidadBruta(
            [FromBody] ReporteriaRequest request,
            [FromQuery] bool sum = false,
            [FromQuery] bool distinct = false,
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 10,
            [FromQuery] string connectionId = null) =>
            await GetReportData(request, sum, distinct, page, pageSize, GetUtilidadBrutaBaseQuery(), ReportType.UtilidadBruta, connectionId);

        #region Enums and Constants
        public enum ReportType { Ventas, Compras, Mermas, Almacen, UtilidadBruta }

        public class ReportConfig
        {
            public string DefaultOrderField { get; set; }
            public string SumOrderField { get; set; }
            public string SumFields { get; set; }
            public string[] IndexedColumns { get; set; } = Array.Empty<string>();
        }

        private static readonly Dictionary<ReportType, ReportConfig> ReportConfigs = new()
        {
            { ReportType.Ventas, new ReportConfig {
                DefaultOrderField = "FechaEmision DESC",
                SumOrderField = "ImporteTotal DESC",
                SumFields = "SUM(Cantidad) AS Cantidad, SUM(ImporteTotal) AS ImporteTotal, SUM(CostoTotal) AS CostoTotal",
                IndexedColumns = new[] { "FechaEmision", "Articulo", "Almacen", "Cliente" }
            }},
            { ReportType.Compras, new ReportConfig {
                DefaultOrderField = "FechaEmision DESC",
                SumOrderField = "CostoTotal DESC",
                SumFields = "SUM(Cantidad) AS Cantidad, SUM(CostoTotal) AS CostoTotal",
                IndexedColumns = new[] { "FechaEmision", "Articulo", "Almacen", "Proveedor" }
            }},
            { ReportType.Mermas, new ReportConfig {
                DefaultOrderField = "Articulo ASC",
                SumOrderField = "TotalImporte DESC",
                SumFields = "SUM(Cantidad) AS Cantidad, SUM(TotalImporte) AS TotalImporte",
                IndexedColumns = new[] { "Articulo", "FechaEmision", "Sucursal" }
            }},
            { ReportType.Almacen, new ReportConfig {
                DefaultOrderField = "Articulo ASC",
                SumOrderField = "TotalImporte DESC",
                SumFields = "SUM(Cantidad) AS Cantidad, SUM(TotalImporte) AS TotalImporte",
                IndexedColumns = new[] { "Articulo", "FechaEmision", "Sucursal" }
            }},
            { ReportType.UtilidadBruta, new ReportConfig {
                DefaultOrderField = "UtilidadBruta DESC",
                SumOrderField = "UtilidadBruta DESC",
                SumFields = "SUM(TotalComprado) AS TotalComprado, " +
                            "SUM(TotalVendido) AS TotalVendido, " +
                            "SUM(CostoTotalCompra) AS CostoTotalCompra, " +
                            "SUM(CostoTotalVenta) AS CostoTotalVenta, " +
                            "SUM(ImporteTotalVenta) AS ImporteTotalVenta, " +
                            "SUM(UtilidadBruta) AS UtilidadBruta, " +
                            "(SUM(ImporteTotalVenta) - SUM(CostoTotalVenta)) AS UtilidadBrutaRecalculada, " +
                            "((SUM(ImporteTotalVenta) - SUM(CostoTotalVenta)) / NULLIF(SUM(ImporteTotalVenta), 0) * 100 AS PorcentajeUtilidadBrutaRecalculada",
                IndexedColumns = new[] { "Articulo", "Nombre" }
            }}
        };
        #endregion

        #region Private Methods

        public async Task<IActionResult> GetReportData(
            ReporteriaRequest request,
            bool sum,
            bool distinct,
            int page,
            int pageSize,
            string baseQuery,
            ReportType reportType,
            string connectionId = null)
        {
            // Validaciones
            if (request == null)
                return BadRequest("La solicitud no puede ser nula.");

            // Notificar inicio del proceso
            if (!string.IsNullOrEmpty(connectionId))
            {
                await _hubContext.Clients.Client(connectionId).SendAsync("ReportProgress",
                    new { Status = "Iniciando", Progress = 0 });
            }

            string cacheKey = null;
            try
            {
                cacheKey = BuildCacheKey(reportType, sum, distinct, page, pageSize, request);
                if (_memoryCache.TryGetValue(cacheKey, out ReportResponse cachedResponse))
                {
                    // Notificar caché encontrado
                    if (!string.IsNullOrEmpty(connectionId))
                    {
                        await _hubContext.Clients.Client(connectionId).SendAsync("ReportProgress",
                            new { Status = "Completado (caché)", Progress = 100 });
                    }
                    return Ok(cachedResponse);
                }
            }
            catch
            {
                cacheKey = null; // Fallback: proceder sin caché si hay error
            }

            // Configuración de paginación
            page = Math.Max(page, 1);
            pageSize = Math.Max(pageSize, 10);
            int offset = (page - 1) * pageSize;

            // Notificar construcción de consultas
            if (!string.IsNullOrEmpty(connectionId))
            {
                await _hubContext.Clients.Client(connectionId).SendAsync("ReportProgress",
                    new { Status = "Construyendo consultas", Progress = 10 });
            }

            // Construcción de consultas optimizadas
            var (whereClauses, parameters) = BuildOptimizedFilters(request.Filtros, reportType);
            var selectColumns = GetSelectColumns(request.Selects);
            var whereQuery = whereClauses.Any() ? $"WHERE {string.Join(" AND ", whereClauses)}" : "";

            // Notificar consultas construidas
            if (!string.IsNullOrEmpty(connectionId))
            {
                await _hubContext.Clients.Client(connectionId).SendAsync("ReportProgress",
                    new { Status = "Consultas construidas", Progress = 20 });
            }

            // Construcción de consultas SQL optimizadas
            var (dataQuery, countQuery) = BuildOptimizedQueries(
                reportType: reportType,
                sum: sum,
                distinct: distinct,
                baseQuery: baseQuery,
                whereQuery: whereQuery,
                selectColumns: selectColumns,
                orderBy: request.OrderBy
            );

            // Notificar inicio de ejecución
            if (!string.IsNullOrEmpty(connectionId))
            {
                await _hubContext.Clients.Client(connectionId).SendAsync("ReportProgress",
                    new { Status = "Ejecutando consultas", Progress = 30 });
            }

            // Ejecución de consultas optimizadas
            try
            {
                var (totalRecords, results) = await ExecuteOptimizedQueryAsync(
                    countQuery: countQuery,
                    dataQuery: dataQuery,
                    parameters: parameters,
                    offset: offset,
                    pageSize: pageSize,
                    connectionId: connectionId
                );

                var response = new ReportResponse
                {
                    TotalRecords = totalRecords,
                    TotalPages = (int)Math.Ceiling(totalRecords / (double)pageSize),
                    Page = page,
                    PageSize = pageSize,
                    Data = results
                };

                // Almacenar en caché si la clave es válida
                if (cacheKey != null)
                {
                    var cacheOptions = new MemoryCacheEntryOptions()
                        .SetSlidingExpiration(TimeSpan.FromMinutes(5))
                        .SetSize(1024); // Limitar tamaño de caché

                    _memoryCache.Set(cacheKey, response, cacheOptions);
                }

                // Notificar finalización
                if (!string.IsNullOrEmpty(connectionId))
                {
                    await _hubContext.Clients.Client(connectionId).SendAsync("ReportProgress",
                        new { Status = "Completado", Progress = 100 });
                }

                return Ok(response);
            }
            catch (Exception ex)
            {
                // Notificar error
                if (!string.IsNullOrEmpty(connectionId))
                {
                    await _hubContext.Clients.Client(connectionId).SendAsync("ReportError",
                        new { Message = ex.Message });
                }
                return HandleException(ex, $"{countQuery}; {dataQuery}");
            }
        }

        public string BuildCacheKey(
            ReportType reportType,
            bool sum,
            bool distinct,
            int page,
            int pageSize,
            ReporteriaRequest request)
        {
            var options = new JsonSerializerOptions { WriteIndented = false };
            var requestJson = JsonSerializer.Serialize(request, options);
            var rawKey = $"{reportType}_{sum}_{distinct}_{page}_{pageSize}_{requestJson}";

            using (var sha1 = SHA1.Create())
            {
                byte[] hash = sha1.ComputeHash(Encoding.UTF8.GetBytes(rawKey));
                var sb = new StringBuilder(40);
                foreach (byte b in hash)
                    sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        public (List<string> whereClauses, List<SqlParameter> parameters) BuildOptimizedFilters(
            List<BusquedaParams> filtros,
            ReportType reportType)
        {
            var whereClauses = new List<string>();
            var parameters = new List<SqlParameter>();
            var parameterCounters = new Dictionary<string, int>();

            // Obtener columnas indexadas para este tipo de reporte
            var indexedColumns = ReportConfigs[reportType].IndexedColumns;

            // Procesamiento especial para rangos de fecha
            var fechaEmisionParams = filtros?.Where(f => f.Key == "FechaEmision").ToList() ?? new List<BusquedaParams>();
            bool fechaRangeProcessed = ProcessDateRange(fechaEmisionParams, whereClauses, parameters);

            // Procesamiento de otros filtros con prioridad para columnas indexadas
            if (filtros != null)
            {
                // Procesar primero las columnas indexadas
                foreach (var filter in filtros
                    .Where(f => f != null && indexedColumns.Contains(f.Key, StringComparer.OrdinalIgnoreCase)))
                {
                    if (fechaRangeProcessed && filter.Key == "FechaEmision") continue;

                    var (clause, param) = BuildOptimizedFilterClause(filter, parameterCounters);
                    if (clause != null)
                    {
                        whereClauses.Add(clause);
                        parameters.Add(param);
                    }
                }

                // Luego procesar las no indexadas
                foreach (var filter in filtros
                    .Where(f => f != null && !indexedColumns.Contains(f.Key, StringComparer.OrdinalIgnoreCase)))
                {
                    if (fechaRangeProcessed && filter.Key == "FechaEmision") continue;

                    var (clause, param) = BuildOptimizedFilterClause(filter, parameterCounters);
                    if (clause != null)
                    {
                        whereClauses.Add(clause);
                        parameters.Add(param);
                    }
                }
            }

            whereClauses = GroupConditions(whereClauses);
            return (whereClauses, parameters);
        }

        public (string clause, SqlParameter param) BuildOptimizedFilterClause(
            BusquedaParams filter,
            Dictionary<string, int> parameterCounters)
        {
            if (string.IsNullOrWhiteSpace(filter.Value)) return (null, null);

            string operatorClause = filter.Operator?.ToUpper() switch
            {
                "LIKE" => "LIKE",
                "=" => "=",
                ">=" => ">=",
                "<=" => "<=",
                ">" => ">",
                "<" => "<",
                "<>" => "<>",
                "IN" => "IN",
                _ => "="
            };

            var column = filter.Key;
            parameterCounters.TryGetValue(column, out int count);
            parameterCounters[column] = count + 1;

            var paramName = $"@{column}_{count}";
            string clause;

            // Manejo especial para operador IN
            if (operatorClause == "IN")
            {
                var values = filter.Value.Split(',');
                var paramNames = new List<string>();
                var parameters = new List<SqlParameter>();
                for (int i = 0; i < values.Length; i++)
                {
                    var inParamName = $"{paramName}_{i}";
                    paramNames.Add(inParamName);
                    parameters.Add(new SqlParameter(inParamName, values[i].Trim()));
                }
                clause = $"{column} IN ({string.Join(", ", paramNames)})";
                return (clause, null);
            }
            else
            {
                clause = $"{column} {operatorClause} {paramName}";
            }

            object paramValue = operatorClause == "LIKE" ? $"%{filter.Value}%" : filter.Value;

            // Manejo especial para tipos de datos
            if (DateTime.TryParse(filter.Value, out var dateValue))
            {
                paramValue = dateValue;
            }
            else if (decimal.TryParse(filter.Value, out var decimalValue))
            {
                paramValue = decimalValue;
            }
            else if (int.TryParse(filter.Value, out var intValue))
            {
                paramValue = intValue;
            }

            return (clause, new SqlParameter(paramName, paramValue));
        }

        public (string dataQuery, string countQuery) BuildOptimizedQueries(
            ReportType reportType,
            bool sum,
            bool distinct,
            string baseQuery,
            string whereQuery,
            string selectColumns,
            OrderParams orderBy = null)
        {
            var config = ReportConfigs[reportType];
            string orderByField = GetOrderByField(orderBy, config, sum, selectColumns);

            if (sum)
            {
                var groupByClause = string.IsNullOrEmpty(selectColumns) ? "" : $"GROUP BY {selectColumns}";

                var dataQuery = $@"
                    WITH SumData AS (
                        SELECT 
                            {(string.IsNullOrEmpty(selectColumns) ? "" : $"{selectColumns},")}
                            {config.SumFields}
                        {baseQuery}
                        {whereQuery}
                        {groupByClause}
                    )
                    SELECT * FROM SumData
                    ORDER BY {orderByField}
                    OFFSET @Offset ROWS FETCH NEXT @PageSize ROWS ONLY";

                string countQuery;
                if (string.IsNullOrEmpty(selectColumns))
                {
                    // Sin agrupación: siempre 1 registro de total
                    countQuery = "SELECT 1 AS TotalRegistros";
                }
                else
                {
                    // Con agrupación: contar grupos generados
                    countQuery = $@"
                        SELECT COUNT(*) AS TotalRegistros
                        FROM (
                            SELECT {selectColumns}
                            {baseQuery}
                            {whereQuery}
                            GROUP BY {selectColumns}
                        ) AS GroupedData";
                }

                return (dataQuery, countQuery);
            }

            if (distinct)
            {
                var dataQuery = $@"
                    SELECT DISTINCT {(string.IsNullOrEmpty(selectColumns) ? "*" : selectColumns)}
                    {baseQuery}
                    {whereQuery}
                    ORDER BY {orderByField}
                    OFFSET @Offset ROWS FETCH NEXT @PageSize ROWS ONLY";

                var countQuery = string.IsNullOrEmpty(selectColumns)
                    ? $"SELECT COUNT(*) AS TotalRegistros FROM (SELECT DISTINCT * {baseQuery} {whereQuery}) AS Subquery"
                    : $"SELECT COUNT(*) AS TotalRegistros FROM (SELECT DISTINCT {selectColumns} {baseQuery} {whereQuery}) AS Subquery";

                return (dataQuery, countQuery);
            }

            // Consulta normal optimizada con paginación eficiente
            var normalDataQuery = $@"
                SELECT 
                    {(string.IsNullOrEmpty(selectColumns) ? "*" : selectColumns)}
                {baseQuery}
                {whereQuery}
                ORDER BY {orderByField}
                OFFSET @Offset ROWS FETCH NEXT @PageSize ROWS ONLY";

            var normalCountQuery = $"SELECT COUNT(*) AS TotalRegistros {baseQuery} {whereQuery}";

            return (normalDataQuery, normalCountQuery);
        }
        // Método para clonar parámetros
        private List<SqlParameter> CloneParameters(List<SqlParameter> parameters)
        {
            return parameters.Select(p => new SqlParameter
            {
                ParameterName = p.ParameterName,
                Value = p.Value,
                SqlDbType = p.SqlDbType,
                Size = p.Size,
                Direction = p.Direction
                // Copia otras propiedades si son necesarias
            }).ToList();
        }
        public async Task<(int totalRecords, List<Dictionary<string, object>> results)> ExecuteOptimizedQueryAsync(
            string countQuery,
            string dataQuery,
            List<SqlParameter> parameters,
            int offset,
            int pageSize,
            string connectionId = null
        )
        {
            // Notificar conexión a la base de datos
            if (!string.IsNullOrEmpty(connectionId))
            {
                await _hubContext.Clients.Client(connectionId).SendAsync("ReportProgress",
                    new { Status = "Conectando a la base de datos", Progress = 40 });
            }

            await using var connection = await OpenConnectionAsync();

            // Ejecutar countQuery primero
            int totalRecords = 0;
            if (!string.IsNullOrEmpty(connectionId))
            {
                await _hubContext.Clients.Client(connectionId).SendAsync("ReportProgress",
                    new { Status = "Contando registros", Progress = 50 });
            }

            // Ejecutar COUNT en un comando separado
            await using (var countCommand = new SqlCommand(countQuery, connection))
            {
                var countParams = CloneParameters(parameters); // Clonar
                countCommand.Parameters.AddRange(countParams.ToArray());
                countCommand.CommandTimeout = 60;
                totalRecords = Convert.ToInt32(await countCommand.ExecuteScalarAsync());
            }

            // Notificar conteo completado
            if (!string.IsNullOrEmpty(connectionId))
            {
                await _hubContext.Clients.Client(connectionId).SendAsync("ReportProgress",
                    new { Status = "Obteniendo datos", Progress = 60 });
            }

            // Ejecutar dataQuery con paginación
            var results = new List<Dictionary<string, object>>();
            await using (var dataCommand = new SqlCommand(dataQuery, connection))
            {
                var dataParams = CloneParameters(parameters); // Clonar
                dataParams.Add(new SqlParameter("@Offset", offset));
                dataParams.Add(new SqlParameter("@PageSize", pageSize));
                dataCommand.Parameters.AddRange(dataParams.ToArray());
                dataCommand.CommandTimeout = 120;

                // Cambiar a CommandBehavior.Default para acceso aleatorio a columnas
                await using var reader = await dataCommand.ExecuteReaderAsync(CommandBehavior.Default);

                int processedRows = 0;
                while (await reader.ReadAsync())
                {
                    var row = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                    for (int i = 0; i < reader.FieldCount; i++)
                    {
                        row[reader.GetName(i)] = reader.GetValue(i) is DBNull ? null : reader.GetValue(i);
                    }
                    results.Add(row);

                    // Notificar progreso cada 100 filas
                    processedRows++;
                    if (!string.IsNullOrEmpty(connectionId) && processedRows % 100 == 0)
                    {
                        int progress = 60 + (int)(40 * (processedRows / (double)pageSize));
                        await _hubContext.Clients.Client(connectionId).SendAsync("ReportProgress",
                            new { Status = "Procesando datos", Progress = progress, ProcessedRows = processedRows });
                    }
                }
            }

            return (totalRecords, results);
        }
        public bool ProcessDateRange(List<BusquedaParams> fechaParams, List<string> whereClauses, List<SqlParameter> parameters)
        {
            if (fechaParams.Count == 2)
            {
                var minFecha = fechaParams.FirstOrDefault(f => f.Operator == ">=");
                var maxFecha = fechaParams.FirstOrDefault(f => f.Operator == "<=");

                if (minFecha != null && maxFecha != null &&
                    DateTime.TryParse(minFecha.Value, out var minDate) &&
                    DateTime.TryParse(maxFecha.Value, out var maxDate))
                {
                    whereClauses.Add("FechaEmision BETWEEN @FechaEmisionMin AND @FechaEmisionMax");
                    parameters.Add(new SqlParameter("@FechaEmisionMin", minDate));
                    parameters.Add(new SqlParameter("@FechaEmisionMax", maxDate));
                    return true;
                }
            }
            return false;
        }
        public string GetSelectColumns(List<SumaParams> selects)
        {
            if (selects == null) return string.Empty;

            return string.Join(", ", selects
                .Where(s => s != null && !string.IsNullOrWhiteSpace(s.Key))
                .Select(s => s.Key));
        }

        public List<string> GroupConditions(List<string> whereClauses)
        {
            return whereClauses
                .GroupBy(c => c.Split(' ', 2)[0])
                .Select(g => g.Count() > 1
                    ? $"({string.Join(" OR ", g)})"
                    : g.First())
                .ToList();
        }
        public string GetOrderByField(OrderParams orderBy, ReportConfig config, bool sum, string selectColumns)
        {
            if (orderBy != null && !string.IsNullOrEmpty(orderBy.Key))
            {
                var direction = string.Equals(orderBy.Direction, "ASC", StringComparison.OrdinalIgnoreCase) ? "ASC" : "DESC";
                return $"{orderBy.Key} {direction}";
            }

            if (sum) return config.SumOrderField;
            if (!string.IsNullOrEmpty(selectColumns)) return selectColumns.Split(',').First().Trim();

            return config.DefaultOrderField;
        }
        #region Base Queries (optimizadas)
        private string GetVentasBaseQuery() => @"
            FROM (
                SELECT 
                    INVD.Codigo,
                    C.Nombre AS Cliente,
                    'VENTA' AS Tipo,
                    INV.Mov AS Movimiento,
                    INVD.Articulo,
                    ART.Descripcion1 AS Nombre,
                    ART.Categoria,
                    ART.Grupo,
                    ART.Linea,
                    ART.Familia,
                    INVD.Unidad,
                    INVD.Factor,
                    (INVD.CantidadInventario / INVD.Cantidad) AS Equivalencia,
                    INVD.Costo AS CostoUnitario,
                    (INVD.Costo * INVD.Cantidad) AS CostoTotal,
                    INVD.Precio AS ImporteUnitario,
                    (INVD.Precio * INVD.Cantidad) AS ImporteTotal,
                    INVD.Cantidad,
                    CASE 
                        WHEN INVD.Almacen = 'ALMVGPE' THEN 'LIZ'
                        WHEN INVD.Almacen = 'ALMPALM' THEN 'PALMAS'
                        WHEN INVD.Almacen = 'ALMTESTE' THEN 'TESTERAZO'
                        WHEN INVD.Almacen = 'ALMMAYO' THEN 'MAYOREO'
                        ELSE INVD.Almacen
                    END AS Almacen,
                    FechaEmision,
                    FORMAT(FechaEmision, 'MMMM', 'es-ES') AS Mes,
                    YEAR(FechaEmision) AS Año
                FROM 
                    [TC032841E].dbo.VENTAD INVD WITH (NOLOCK)
                INNER JOIN 
                    [TC032841E].dbo.VENTA INV WITH (NOLOCK) ON INVD.ID = INV.ID
                LEFT JOIN 
                    [TC032841E].dbo.ART WITH (NOLOCK) ON INVD.Articulo = ART.Articulo
                LEFT JOIN 
                    [TC032841E].dbo.Cte C WITH (NOLOCK) ON INV.Cliente = C.Cliente
                WHERE 
                    INV.Mov NOT IN ('FACTURA GLOBAL', 'FACTURA SUCURSAL', 'FACTURA')
                    AND INV.Estatus IN ('CONCLUIDO')
            ) AS VentasReport";

        private string GetComprasBaseQuery() => @"
            FROM (
                SELECT
                    INVD.Codigo, 
                    C.Nombre AS Proveedor,
                    ART.Fabricante,
                    'COMPRA' AS Tipo,
                    INV.Mov AS Movimiento,
                    INVD.Articulo,
                    ART.Descripcion1 AS Nombre,
                    ART.Categoria,
                    ART.Grupo,
                    ART.Linea,
                    ART.Familia,
                    INVD.Unidad,
                    INVD.Factor,
                    (INVD.CantidadInventario / INVD.Cantidad) AS Equivalencia,
                    INVD.Cantidad,
                    INVD.CantidadInventario,
                    INVD.Costo AS CostoUnitario,
                    (INVD.Costo * INVD.Cantidad) AS CostoTotal,
                    CASE 
                        WHEN INVD.Almacen = 'ALMVGPE' THEN 'LIZ'
                        WHEN INVD.Almacen = 'ALMPALM' THEN 'PALMAS'
                        WHEN INVD.Almacen = 'ALMTESTE' THEN 'TESTERAZO'
                        WHEN INVD.Almacen = 'ALMMAYO' THEN 'MAYOREO'
                        ELSE INVD.Almacen
                    END AS Almacen,
                    INVD.Impuesto1 AS IVA,
                    INVD.Impuesto2 AS IEPS,
                    FORMAT((INVD.DescuentoImporte / NULLIF(INVD.COSTO * INVD.Cantidad, 0)) * 100, 'N2') AS PorcentajeDescuento,
                    FechaEmision,
                    FORMAT(FechaEmision, 'MMMM', 'es-ES') AS Mes,
                    YEAR(FechaEmision) AS Año
                FROM 
                    [TC032841E].dbo.COMPRAD InvD WITH (NOLOCK)
                INNER JOIN 
                    [TC032841E].dbo.COMPRA INV WITH (NOLOCK) ON INVD.ID = INV.ID
                LEFT JOIN 
                    [TC032841E].dbo.ART WITH (NOLOCK) ON INVD.Articulo = ART.Articulo
                LEFT JOIN 
                    [TC032841E].dbo.PROV C WITH (NOLOCK) ON INV.Proveedor = C.Proveedor
                WHERE 
                    INV.Mov = 'ENTRADA COMPRA'
                    AND INV.Estatus = 'CONCLUIDO'
            ) AS ComprasReport";

        private string GetMermasBaseQuery() => @"
            FROM (
                SELECT 
                    art.Articulo,
                    art.Descripcion1 AS Nombre,
                    art.Categoria,
                    art.Grupo,
                    art.Linea,
                    art.Familia,
                    inv.Concepto,
                    invd.Cantidad,
                    invd.Costo,
                    invd.Unidad,
                    SUM(invd.Cantidad) OVER (PARTITION BY invd.Articulo, inv.FechaEmision) AS TotalCantidad,
                    SUM(invd.Costo * invd.Cantidad) OVER (PARTITION BY invd.Articulo, inv.FechaEmision) AS TotalImporte,
                    inv.Sucursal,
                    inv.movid,
                    inv.estatus,
                    inv.FechaEmision,
                    FORMAT(inv.FechaEmision, 'dd', 'es-ES') AS Dia,
                    FORMAT(inv.FechaEmision, 'MMMM', 'es-ES') AS Mes,
                    YEAR(inv.FechaEmision) AS Año
                FROM 
                    [TC032841E].dbo.INVD invd WITH (NOLOCK)
                INNER JOIN 
                    [TC032841E].dbo.inv inv WITH (NOLOCK) ON inv.ID = invd.ID 
                LEFT JOIN 
                    [TC032841E].dbo.Art art WITH (NOLOCK) ON art.Articulo = invd.Articulo
                WHERE 
                    inv.Concepto LIKE '%MERMAS%'
                    AND inv.Estatus = 'CONCLUIDO'
            ) AS MermasReport";

        private string GetUtilidadBrutaBaseQuery() => @"
            FROM (
                SELECT 
                    V.Articulo,
                    A.Descripcion1 AS Nombre,
                    ISNULL(C.TotalComprado, 0) AS TotalComprado,
                    ISNULL(V.TotalVendido, 0) AS TotalVendido,
                    ISNULL(C.CostoTotalCompra, 0) AS CostoTotalCompra,
                    ISNULL(V.CostoTotalVenta, 0) AS CostoTotalVenta,
                    ISNULL(V.ImporteTotalVenta, 0) AS ImporteTotalVenta,
                    (ISNULL(V.ImporteTotalVenta, 0) - ISNULL(V.CostoTotalVenta, 0)) AS UtilidadBruta,
                    CASE 
                        WHEN ISNULL(V.ImporteTotalVenta, 0) = 0 THEN 0
                        ELSE (ISNULL(V.ImporteTotalVenta, 0) - ISNULL(V.CostoTotalVenta, 0)) * 100.0 / V.ImporteTotalVenta 
                    END AS PorcentajeUtilidadBruta
                FROM (
                    SELECT 
                        d.Articulo,
                        SUM(d.Cantidad) AS TotalVendido,
                        SUM(d.Costo * d.Cantidad) AS CostoTotalVenta,
                        SUM(d.Precio * d.Cantidad) AS ImporteTotalVenta
                    FROM [TC032841E].dbo.VENTAD d WITH (NOLOCK)
                    INNER JOIN [TC032841E].dbo.VENTA v WITH (NOLOCK)
                        ON d.ID = v.ID
                        AND v.Estatus = 'CONCLUIDO'
                        AND v.Mov NOT IN ('FACTURA GLOBAL', 'FACTURA SUCURSAL', 'FACTURA')
                    GROUP BY d.Articulo
                ) V
                LEFT JOIN (
                    SELECT 
                        d.Articulo,
                        SUM(d.Cantidad) AS TotalComprado,
                        SUM(d.Costo * d.Cantidad) AS CostoTotalCompra
                    FROM [TC032841E].dbo.COMPRAD d WITH (NOLOCK)
                    INNER JOIN [TC032841E].dbo.COMPRA c WITH (NOLOCK)
                        ON d.ID = c.ID
                        AND c.Estatus = 'CONCLUIDO'
                        AND c.Mov = 'ENTRADA COMPRA'
                    GROUP BY d.Articulo
                ) C ON V.Articulo = C.Articulo
                LEFT JOIN [TC032841E].dbo.ART A WITH (NOLOCK)
                    ON V.Articulo = A.Articulo
            ) AS UtilidadBrutaReport";

        private string GetAlmacenBaseQuery() => @"
            FROM (
                SELECT 
                    art.Articulo,
                    art.Descripcion1 AS Nombre,
                    art.Categoria,
                    art.Grupo,
                    art.Linea,
                    art.Familia,
                    inv.Concepto,
                    invd.Cantidad,
                    invd.Costo,
                    invd.Unidad,
                    SUM(invd.Cantidad) OVER (PARTITION BY invd.Articulo, inv.FechaEmision) AS TotalCantidad,
                    SUM(invd.Costo * invd.Cantidad) OVER (PARTITION BY invd.Articulo, inv.FechaEmision) AS TotalImporte,
                    inv.Sucursal,
                    inv.movid,
                    inv.estatus,
                    inv.FechaEmision,
                    FORMAT(inv.FechaEmision, 'dd', 'es-ES') AS Dia,
                    FORMAT(inv.FechaEmision, 'MMMM', 'es-ES') AS Mes,
                    YEAR(inv.FechaEmision) AS Año
                FROM 
                    [TC032841E].dbo.INVD invd WITH (NOLOCK)
                INNER JOIN 
                    [TC032841E].dbo.inv inv WITH (NOLOCK) ON inv.ID = invd.ID 
                LEFT JOIN 
                    [TC032841E].dbo.Art art WITH (NOLOCK) ON art.Articulo = invd.Articulo
                WHERE 
                    inv.Estatus = 'CONCLUIDO'
            ) AS AlmacenReport";
        #endregion
    }

    // Hub de SignalR para notificaciones de progreso
    public class ReportHub : Hub
    {
        public async Task RegisterForProgress(string reportId)
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, reportId);
        }
    }

    public class ReporteriaRequest
    {
        public List<BusquedaParams> Filtros { get; set; } = new List<BusquedaParams>();
        public List<SumaParams> Selects { get; set; } = new List<SumaParams>();
        public OrderParams OrderBy { get; set; }
    }

    public class ReportResponse
    {
        public int TotalRecords { get; set; }
        public int TotalPages { get; set; }
        public int Page { get; set; }
        public int PageSize { get; set; }
        public List<Dictionary<string, object>> Data { get; set; }
    }
    #endregion
}