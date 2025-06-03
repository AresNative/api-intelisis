using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Caching.Memory;
using MyApiProject.Models;
using System.Data;

namespace MyApiProject.Controllers
{
    public partial class Reporteria : BaseController
    {
        private readonly IMemoryCache _memoryCache;

        public Reporteria(IConfiguration configuration, IMemoryCache memoryCache) : base(configuration)
        {
            _memoryCache = memoryCache;
        }

        [HttpPost("api/v2/reporteria/ventas")]
        public async Task<IActionResult> ObtenerVentas(
            [FromBody] ReporteriaRequest request,
            [FromQuery] bool sum = false,
            [FromQuery] bool distinct = false,
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 10) =>
            await GetReportData(request, sum, distinct, page, pageSize, GetVentasBaseQuery(), ReportType.Ventas);

        [HttpPost("api/v2/reporteria/compras")]
        public async Task<IActionResult> ObtenerCompras(
            [FromBody] ReporteriaRequest request,
            [FromQuery] bool sum = false,
            [FromQuery] bool distinct = false,
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 10) =>
            await GetReportData(request, sum, distinct, page, pageSize, GetComprasBaseQuery(), ReportType.Compras);

        [HttpPost("api/v2/reporteria/mermas")]
        public async Task<IActionResult> ObtenerMermas(
            [FromBody] ReporteriaRequest request,
            [FromQuery] bool sum = false,
            [FromQuery] bool distinct = false,
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 10) =>
            await GetReportData(request, sum, distinct, page, pageSize, GetMermasBaseQuery(), ReportType.Mermas);

        #region Enums and Constants
        private enum ReportType { Ventas, Compras, Mermas }

        private class ReportConfig
        {
            public string DefaultOrderField { get; set; }
            public string SumOrderField { get; set; }
            public string SumFields { get; set; }
        }

        private static readonly Dictionary<ReportType, ReportConfig> ReportConfigs = new()
        {
            { ReportType.Ventas, new ReportConfig {
                DefaultOrderField = "FechaEmision DESC",
                SumOrderField = "ImporteTotal DESC",
                SumFields = "SUM(Cantidad) AS Cantidad, SUM(ImporteTotal) AS ImporteTotal, SUM(CostoTotal) AS CostoTotal"
            }},
            { ReportType.Compras, new ReportConfig {
                DefaultOrderField = "FechaEmision DESC",
                SumOrderField = "CostoTotal DESC",
                SumFields = "SUM(Cantidad) AS Cantidad, SUM(CostoTotal) AS CostoTotal"
            }},
            { ReportType.Mermas, new ReportConfig {
                DefaultOrderField = "Articulo ASC",
                SumOrderField = "TotalImporte DESC",
                SumFields = "SUM(Cantidad) AS Cantidad, SUM(TotalImporte) AS TotalImporte"
            }}
        };
        #endregion

        #region Private Methods
        private async Task<IActionResult> GetReportData(
            ReporteriaRequest request,
            bool sum,
            bool distinct,
            int page,
            int pageSize,
            string baseQuery,
            ReportType reportType)
        {
            // Validaciones
            if (sum && distinct)
                return BadRequest("Los parámetros sum y distinct no pueden ser verdaderos al mismo tiempo.");

            if (request == null)
                return BadRequest("La solicitud no puede ser nula.");

            // Configuración de paginación
            page = Math.Max(page, 1);
            pageSize = Math.Max(pageSize, 10);
            int offset = (page - 1) * pageSize;

            // Construcción de consultas
            var (whereClauses, parameters) = BuildFilters(request.Filtros);
            var selectColumns = GetSelectColumns(request.Selects);
            var whereQuery = whereClauses.Any() ? $"WHERE {string.Join(" AND ", whereClauses)}" : "";

            // Construcción de consultas SQL
            var (dataQuery, countQuery) = BuildQueries(
                reportType: reportType,
                sum: sum,
                distinct: distinct,
                baseQuery: baseQuery,
                whereQuery: whereQuery,
                selectColumns: selectColumns,
                orderBy: request.OrderBy
            );

            // Ejecución de consultas
            try
            {
                var (totalRecords, results) = await ExecuteQueryAsync(
                    $"{countQuery}; {dataQuery}",
                    parameters,
                    offset,
                    pageSize
                );

                return Ok(new ReportResponse
                {
                    TotalRecords = totalRecords,
                    TotalPages = (int)Math.Ceiling(totalRecords / (double)pageSize),
                    Page = page,
                    PageSize = pageSize,
                    Data = results
                });
            }
            catch (Exception ex)
            {
                return HandleException(ex, $"{countQuery}; {dataQuery}");
            }
        }

        private (List<string> whereClauses, List<SqlParameter> parameters) BuildFilters(List<BusquedaParams> filtros)
        {
            var whereClauses = new List<string>();
            var parameters = new List<SqlParameter>();
            var parameterCounters = new Dictionary<string, int>();

            // Procesamiento especial para rangos de fecha
            var fechaEmisionParams = filtros?.Where(f => f.Key == "FechaEmision").ToList() ?? new List<BusquedaParams>();
            bool fechaRangeProcessed = ProcessDateRange(fechaEmisionParams, whereClauses, parameters);

            // Procesamiento de otros filtros
            if (filtros != null)
            {
                foreach (var filter in filtros.Where(f => f != null))
                {
                    if (fechaRangeProcessed && filter.Key == "FechaEmision") continue;

                    var (clause, param) = BuildFilterClause(filter, parameterCounters);
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

        private (string clause, SqlParameter param) BuildFilterClause(BusquedaParams filter, Dictionary<string, int> parameterCounters)
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
                _ => "LIKE"
            };

            var column = filter.Key;
            parameterCounters.TryGetValue(column, out int count);
            parameterCounters[column] = count + 1;

            var paramName = $"@{column}_{count}";
            var clause = $"{column} {operatorClause} {paramName}";

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

            return (clause, new SqlParameter(paramName, paramValue));
        }

        private List<string> GroupConditions(List<string> whereClauses)
        {
            return whereClauses
                .GroupBy(c => c.Split(' ', 2)[0])
                .Select(g => g.Count() > 1
                    ? $"({string.Join(" OR ", g)})"
                    : g.First())
                .ToList();
        }

        private string GetSelectColumns(List<SumaParams> selects)
        {
            if (selects == null) return string.Empty;

            return string.Join(", ", selects
                .Where(s => s != null && !string.IsNullOrWhiteSpace(s.Key))
                .Select(s => s.Key));
        }

        private (string dataQuery, string countQuery) BuildQueries(
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
                var distinctCount = string.IsNullOrEmpty(selectColumns) ? "ID" : GetDistinctColumns(selectColumns);

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

                var countQuery = $"SELECT COUNT(DISTINCT {distinctCount}) AS TotalRegistros {baseQuery} {whereQuery}";

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

            // Consulta normal
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

        private string GetOrderByField(OrderParams orderBy, ReportConfig config, bool sum, string selectColumns)
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

        private async Task<(int totalRecords, List<Dictionary<string, object>> results)> ExecuteQueryAsync(
    string combinedQuery,
    List<SqlParameter> parameters,
    int offset,
    int pageSize)
        {
            await using var connection = await OpenConnectionAsync();
            await using var command = new SqlCommand(combinedQuery, connection)
            {
                CommandTimeout = 120
            };

            command.Parameters.AddRange(parameters.ToArray());
            command.Parameters.Add(new SqlParameter("@Offset", offset));
            command.Parameters.Add(new SqlParameter("@PageSize", pageSize));

            // Cambiamos a CommandBehavior.Default ya que necesitamos acceso aleatorio a las columnas
            await using var reader = await command.ExecuteReaderAsync(CommandBehavior.Default);

            int totalRecords = 0;
            if (await reader.ReadAsync())
            {
                totalRecords = Convert.ToInt32(reader["TotalRegistros"]);
            }

            await reader.NextResultAsync();

            var results = new List<Dictionary<string, object>>();
            while (await reader.ReadAsync())
            {
                var row = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < reader.FieldCount; i++)
                {
                    row[reader.GetName(i)] = reader.GetValue(i) is DBNull ? null : reader.GetValue(i);
                }
                results.Add(row);
            }

            return (totalRecords, results);
        }

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
                    [TC032841E].dbo.VENTAD INVD 
                LEFT JOIN 
                    [TC032841E].dbo.ART ON INVD.Articulo = ART.Articulo
                LEFT JOIN 
                    [TC032841E].dbo.VENTA INV ON INVD.ID = INV.ID
                LEFT JOIN 
                    [TC032841E].dbo.Cte C ON INV.Cliente = C.Cliente
                WHERE 
                    INV.Mov NOT IN ('FACTURA GLOBAL', 'FACTURA SUCURSAL', 'FACTURA')
                    AND INV.Estatus IN ('CONCLUIDO')
            ) AS VentasReport";

        private string GetComprasBaseQuery() => "FROM [TC032841E].[dbo].[Temp_ComprasReport]";

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
                    [TC032841E].dbo.INVD invd
                LEFT JOIN 
                    [TC032841E].dbo.inv inv ON inv.ID = invd.ID 
                LEFT JOIN 
                    [TC032841E].dbo.Art art ON art.Articulo = invd.Articulo
                WHERE 
                    inv.Concepto LIKE '%MERMAS%'
                    AND inv.Estatus = 'CONCLUIDO'
            ) AS MermasReport";

        private string GetDistinctColumns(string columns)
        {
            return string.IsNullOrEmpty(columns) ? "ID" : columns.Split(',').First().Trim();
        }
        #endregion
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
}