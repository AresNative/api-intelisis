using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Caching.Memory;
using System.Data;
using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using MyApiProject.Models;

namespace MyApiProject.Controllers.reporteria
{
    [ApiExplorerSettings(GroupName = "reporteria")]
    [Route("api/v2")]
    [ApiController]
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

        [HttpPost("ventas")]
        public async Task<IActionResult> ObtenerVentas(
            [FromBody] ReporteriaRequest request,
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 10,
            [FromQuery] string? connectionId = null) =>
            await GetReportData(request, page, pageSize, GetVentasBaseQuery(), ReportType.Ventas, connectionId);

        [HttpPost("compras")]
        public async Task<IActionResult> ObtenerCompras(
            [FromBody] ReporteriaRequest request,
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 10,
            [FromQuery] string? connectionId = null) =>
            await GetReportData(request, page, pageSize, GetComprasBaseQuery(), ReportType.Compras, connectionId);

        [HttpPost("mermas")]
        public async Task<IActionResult> ObtenerMermas(
            [FromBody] ReporteriaRequest request,
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 10,
            [FromQuery] string? connectionId = null) =>
            await GetReportData(request, page, pageSize, GetMermasBaseQuery(), ReportType.Mermas, connectionId);

        [HttpPost("almacen")]
        public async Task<IActionResult> ObtenerAlmacen(
            [FromBody] ReporteriaRequest request,
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 10,
            [FromQuery] string? connectionId = null) =>
            await GetReportData(request, page, pageSize, GetAlmacenBaseQuery(), ReportType.Almacen, connectionId);

        [HttpPost("utilidadbruta")]
        public async Task<IActionResult> ObtenerUtilidadBruta(
            [FromBody] ReporteriaRequest request,
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 10,
            [FromQuery] string? connectionId = null) =>
            await GetReportData(request, page, pageSize, GetUtilidadBrutaBaseQuery(), ReportType.UtilidadBruta, connectionId);

        #region Enums and Constants
        public enum ReportType { Ventas, Compras, Mermas, Almacen, UtilidadBruta }

        public class ReportConfig
        {
            public string DefaultOrderField { get; set; } = string.Empty;
            public string SumOrderField { get; set; } = string.Empty;
            public string SumFields { get; set; } = string.Empty;
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

        private async Task<IActionResult> GetReportData(
            ReporteriaRequest request,
            int page,
            int pageSize,
            string baseQuery,
            ReportType reportType,
            string? connectionId = null)
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

            string? cacheKey = null;
            try
            {
                cacheKey = BuildCacheKey(reportType, page, pageSize, request);
                if (_memoryCache.TryGetValue(cacheKey, out ReportResponse? cachedResponse) && cachedResponse != null)
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

            // Construcción de consultas usando métodos de BaseController
            var whereClauses = new List<string>();
            var parameters = new List<SqlParameter>();
            var parameterCounters = new Dictionary<string, int>();

            // Usar BuildFilters de BaseController
            BuildFilters(new FiltrosRequest { Filtros = request.Filtros }, whereClauses, parameters, parameterCounters);

            // Usar BuildSelectClause de BaseController
            var filtrosRequest = new FiltrosRequest
            {
                Selects = request.Selects,
                Agregaciones = request.Agregaciones
            };
            var (selectClause, groupByClause) = BuildSelectClause(filtrosRequest);

            // Usar AgruparCondiciones de BaseController
            var groupedWhereClauses = AgruparCondiciones(whereClauses);
            var whereQuery = groupedWhereClauses.Any() ? $"WHERE {string.Join(" AND ", groupedWhereClauses)}" : "";

            // Usar BuildOrderByClause de BaseController
            var orderList = request.OrderBy != null ?
                new List<OrderParams> { request.OrderBy } : new List<OrderParams>();
            string orderByClause = BuildOrderByClause(new FiltrosRequest { Order = orderList });

            // Notificar consultas construidas
            if (!string.IsNullOrEmpty(connectionId))
            {
                await _hubContext.Clients.Client(connectionId).SendAsync("ReportProgress",
                    new { Status = "Consultas construidas", Progress = 20 });
            }

            // Construcción de consultas SQL
            var countQuery = $@"SELECT COUNT(*) AS TotalRegistros {baseQuery} {whereQuery}";

            var paginatedQuery = $@"
                SELECT {selectClause}
                {baseQuery} {whereQuery}
                {groupByClause}
                {orderByClause}
                OFFSET @Offset ROWS FETCH NEXT @PageSize ROWS ONLY";

            // Notificar inicio de ejecución
            if (!string.IsNullOrEmpty(connectionId))
            {
                await _hubContext.Clients.Client(connectionId).SendAsync("ReportProgress",
                    new { Status = "Ejecutando consultas", Progress = 30 });
            }

            // Ejecución de consultas
            try
            {
                await using var connection = await OpenConnectionAsync();

                // Total records
                var countCommandParameters = parameters
                    .Select(p => new SqlParameter(p.ParameterName, p.Value))
                    .ToList();

                await using var countCommand = new SqlCommand(countQuery, connection);
                countCommand.Parameters.AddRange(countCommandParameters.ToArray());
                var totalRecords = (int)await countCommand.ExecuteScalarAsync();

                // Paginated data
                var paginatedParameters = parameters
                    .Select(p => new SqlParameter(p.ParameterName, p.Value))
                    .ToList();

                paginatedParameters.AddRange(new[]
                {
                    new SqlParameter("@Offset", offset),
                    new SqlParameter("@PageSize", pageSize)
                });

                await using var command = new SqlCommand(paginatedQuery, connection);
                command.Parameters.AddRange(paginatedParameters.ToArray());

                var results = new List<Dictionary<string, object>>();

                await using var reader = await command.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    var row = new Dictionary<string, object>();
                    for (int i = 0; i < reader.FieldCount; i++)
                    {
                        row[reader.GetName(i)] = reader.GetValue(i);
                    }
                    results.Add(row);
                }

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
                        .SetSize(1024);

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
                return HandleException(ex, $"{countQuery}; {paginatedQuery}");
            }
        }

        private string BuildCacheKey(
            ReportType reportType,
            int page,
            int pageSize,
            ReporteriaRequest request)
        {
            var options = new JsonSerializerOptions { WriteIndented = false };

            // Serializar solo las partes relevantes para la clave de caché
            var cacheData = new
            {
                Filtros = request.Filtros?.Where(f => !string.IsNullOrWhiteSpace(f.Value)),
                Selects = request.Selects,
                Agregaciones = request.Agregaciones,
                OrderBy = request.OrderBy
            };

            var requestJson = JsonSerializer.Serialize(cacheData, options);
            var rawKey = $"{reportType}_{page}_{pageSize}_{requestJson}";

            using (var sha1 = SHA1.Create())
            {
                byte[] hash = sha1.ComputeHash(Encoding.UTF8.GetBytes(rawKey));
                var sb = new StringBuilder(40);
                foreach (byte b in hash)
                    sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        // Método para clonar parámetros (se mantiene ya que es específico para esta clase)
        private List<SqlParameter> CloneParameters(List<SqlParameter> parameters)
        {
            return parameters.Select(p => new SqlParameter
            {
                ParameterName = p.ParameterName,
                Value = p.Value,
                SqlDbType = p.SqlDbType,
                Size = p.Size,
                Direction = p.Direction
            }).ToList();
        }

        // Método específico para procesar rangos de fecha (ahora privado)
        private bool ProcessDateRange(List<BusquedaParams> fechaParams, List<string> whereClauses, List<SqlParameter> parameters)
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
        public List<SelectParams> Selects { get; set; } = new List<SelectParams>();
        public List<AgregacionParams> Agregaciones { get; set; } = new List<AgregacionParams>();
        public OrderParams? OrderBy { get; set; }
    }

    public class ReportResponse
    {
        public int TotalRecords { get; set; }
        public int TotalPages { get; set; }
        public int Page { get; set; }
        public int PageSize { get; set; }
        public List<Dictionary<string, object>> Data { get; set; } = new List<Dictionary<string, object>>();
    }
    #endregion
}