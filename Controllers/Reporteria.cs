using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Caching.Memory;
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
          [FromQuery] int pageSize = 10)
        {
            if (sum && distinct)
            {
                return BadRequest("Los parámetros sum y distinct no pueden ser verdaderos al mismo tiempo.");
            }

            page = Math.Max(page, 1);
            pageSize = Math.Max(pageSize, 10);
            int offset = (page - 1) * pageSize;

            const string baseQuery = @"
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

            var whereClauses = new List<string>();
            var sumaClauses = new List<string>();
            var parameters = new List<SqlParameter>();
            var parameterCounters = new Dictionary<string, int>();

            BuildFilters(request, whereClauses, parameters, parameterCounters);

            foreach (var suma in request.Selects)
            {
                if (!string.IsNullOrWhiteSpace(suma.Key))
                    sumaClauses.Add(suma.Key);
            }

            var condicionesAgrupadas = AgruparCondiciones(whereClauses);
            string whereQuery = condicionesAgrupadas.Any() ? $"WHERE {string.Join(" AND ", condicionesAgrupadas)}" : "";
            string sumaQuery = sumaClauses.Any() ? string.Join(", ", sumaClauses) : "";

            string dataQuery;
            string countQuery;

            if (sum)
            {
                dataQuery = $@"
                    WITH SumData AS (
                        SELECT 
                            {(string.IsNullOrEmpty(sumaQuery) ? "" : $"{sumaQuery},")}
                            SUM(Cantidad) AS Cantidad,
                            SUM(ImporteTotal) AS ImporteTotal,
                            SUM(CostoTotal) AS CostoTotal
                        {baseQuery}
                        {whereQuery}
                        {(string.IsNullOrEmpty(sumaQuery) ? "" : $"GROUP BY {sumaQuery}")}
                    )
                    SELECT * FROM SumData
                    ORDER BY ImporteTotal DESC
                    OFFSET @Offset ROWS FETCH NEXT @PageSize ROWS ONLY";

                countQuery = $"SELECT COUNT(DISTINCT {GetDistinctColumns(sumaQuery)}) AS TotalRegistros {baseQuery} {whereQuery}";
            }
            else if (distinct)
            {
                dataQuery = $@"
                    SELECT DISTINCT {(string.IsNullOrEmpty(sumaQuery) ? "*" : sumaQuery)}
                    {baseQuery}
                    {whereQuery}
                    ORDER BY {(string.IsNullOrEmpty(sumaQuery) ? "FechaEmision" : sumaQuery)} DESC
                    OFFSET @Offset ROWS FETCH NEXT @PageSize ROWS ONLY";

                countQuery = string.IsNullOrEmpty(sumaQuery)
                    ? $"SELECT COUNT(*) AS TotalRegistros FROM (SELECT DISTINCT * {baseQuery} {whereQuery}) AS Subquery"
                    : $"SELECT COUNT(*) AS TotalRegistros FROM (SELECT DISTINCT {sumaQuery} {baseQuery} {whereQuery}) AS Subquery";
            }
            else
            {
                dataQuery = $@"
                    SELECT 
                        {(string.IsNullOrEmpty(sumaQuery) ? "*" : sumaQuery)}
                    {baseQuery}
                    {whereQuery}
                    ORDER BY FechaEmision DESC
                    OFFSET @Offset ROWS FETCH NEXT @PageSize ROWS ONLY";

                countQuery = $"SELECT COUNT(*) AS TotalRegistros {baseQuery} {whereQuery}";
            }

            string combinedQuery = $"{countQuery}; {dataQuery}";

            parameters.Add(new SqlParameter("@Offset", offset));
            parameters.Add(new SqlParameter("@PageSize", pageSize));

            try
            {
                await using var connection = await OpenConnectionAsync();
                await using var command = new SqlCommand(combinedQuery, connection)
                {
                    CommandTimeout = 120 // Aumentado por complejidad de consulta
                };
                command.Parameters.AddRange(parameters.ToArray());

                await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess);

                int totalRecords = 0;
                if (await reader.ReadAsync())
                {
                    totalRecords = Convert.ToInt32(reader["TotalRegistros"]);
                }

                await reader.NextResultAsync();

                var results = new List<Dictionary<string, object>>();
                while (await reader.ReadAsync())
                {
                    var row = new Dictionary<string, object>(reader.FieldCount);
                    object[] values = new object[reader.FieldCount];
                    reader.GetValues(values);
                    for (int i = 0; i < reader.FieldCount; i++)
                    {
                        row[reader.GetName(i)] = values[i];
                    }
                    results.Add(row);
                }

                return Ok(new
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
                return HandleException(ex, combinedQuery);
            }
        }

        [HttpPost("api/v2/reporteria/compras")]
        public async Task<IActionResult> ObtenerCompras(
             [FromBody] ReporteriaRequest request,
             [FromQuery] bool sum = false,
             [FromQuery] bool distinct = false,
             [FromQuery] int page = 1,
             [FromQuery] int pageSize = 10)
        {
            if (sum && distinct)
            {
                return BadRequest("Los parámetros sum y distinct no pueden ser verdaderos al mismo tiempo.");
            }

            page = Math.Max(page, 1);
            pageSize = Math.Max(pageSize, 10);
            int offset = (page - 1) * pageSize;

            const string baseQuery = "FROM [LOCAL_TC032391E].[dbo].[Temp_ComprasReport]";

            var whereClauses = new List<string>();
            var sumaClauses = new List<string>();
            var parameters = new List<SqlParameter>();
            var parameterCounters = new Dictionary<string, int>();

            BuildFilters(request, whereClauses, parameters, parameterCounters);
            foreach (var suma in request.Selects)
            {
                if (!string.IsNullOrWhiteSpace(suma.Key))
                    sumaClauses.Add(suma.Key);
            }

            var condicionesAgrupadas = AgruparCondiciones(whereClauses);
            string whereQuery = condicionesAgrupadas.Any() ? $"WHERE {string.Join(" AND ", condicionesAgrupadas)}" : "";
            string sumaQuery = sumaClauses.Any() ? string.Join(", ", sumaClauses) : "";

            string dataQuery;
            string countQuery;

            if (sum)
            {
                dataQuery = $@"
                    WITH SumData AS (
                        SELECT 
                            {(string.IsNullOrEmpty(sumaQuery) ? "" : $"{sumaQuery},")}
                            SUM(Cantidad) AS Cantidad,
                            SUM(CostoTotal) AS CostoTotal
                        {baseQuery}
                        {whereQuery}
                        {(string.IsNullOrEmpty(sumaQuery) ? "" : $"GROUP BY {sumaQuery}")}
                    )
                    SELECT * FROM SumData
                    ORDER BY CostoTotal DESC
                    OFFSET @Offset ROWS FETCH NEXT @PageSize ROWS ONLY";

                countQuery = $"SELECT COUNT(DISTINCT {GetDistinctColumns(sumaQuery)}) AS TotalRegistros {baseQuery} {whereQuery}";
            }
            else if (distinct)
            {
                dataQuery = $@"
                    SELECT DISTINCT {(string.IsNullOrEmpty(sumaQuery) ? "*" : sumaQuery)}
                    {baseQuery}
                    {whereQuery}
                    ORDER BY {(string.IsNullOrEmpty(sumaQuery) ? "FechaEmision" : sumaQuery)} DESC
                    OFFSET @Offset ROWS FETCH NEXT @PageSize ROWS ONLY";

                countQuery = string.IsNullOrEmpty(sumaQuery)
                    ? $"SELECT COUNT(*) AS TotalRegistros FROM (SELECT DISTINCT * {baseQuery} {whereQuery}) AS Subquery"
                    : $"SELECT COUNT(*) AS TotalRegistros FROM (SELECT DISTINCT {sumaQuery} {baseQuery} {whereQuery}) AS Subquery";
            }
            else
            {
                dataQuery = $@"
                    SELECT 
                        {(string.IsNullOrEmpty(sumaQuery) ? "*" : sumaQuery)}
                    {baseQuery}
                    {whereQuery}
                    ORDER BY FechaEmision DESC
                    OFFSET @Offset ROWS FETCH NEXT @PageSize ROWS ONLY";

                countQuery = $"SELECT COUNT(*) AS TotalRegistros {baseQuery} {whereQuery}";
            }

            string combinedQuery = $"{countQuery}; {dataQuery}";

            parameters.Add(new SqlParameter("@Offset", offset));
            parameters.Add(new SqlParameter("@PageSize", pageSize));

            try
            {
                await using var connection = await OpenConnectionAsync();
                await using var command = new SqlCommand(combinedQuery, connection)
                {
                    CommandTimeout = 120
                };
                command.Parameters.AddRange(parameters.ToArray());

                await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess);

                int totalRecords = 0;
                if (await reader.ReadAsync())
                {
                    totalRecords = Convert.ToInt32(reader["TotalRegistros"]);
                }

                await reader.NextResultAsync();

                var results = new List<Dictionary<string, object>>();
                while (await reader.ReadAsync())
                {
                    var row = new Dictionary<string, object>(reader.FieldCount);
                    object[] values = new object[reader.FieldCount];
                    reader.GetValues(values);
                    for (int i = 0; i < reader.FieldCount; i++)
                    {
                        row[reader.GetName(i)] = values[i];
                    }
                    results.Add(row);
                }

                return Ok(new
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
                return HandleException(ex, combinedQuery);
            }
        }
        [HttpPost("api/v2/reporteria/mermas")]
        public async Task<IActionResult> ObtenerMermas(
                  [FromBody] ReporteriaRequest request,
                  [FromQuery] bool sum = false,
                  [FromQuery] bool distinct = false,
                  [FromQuery] int page = 1,
                  [FromQuery] int pageSize = 10)
        {
            if (sum && distinct)
            {
                return BadRequest("Los parámetros sum y distinct no pueden ser verdaderos al mismo tiempo.");
            }

            page = Math.Max(page, 1);
            pageSize = Math.Max(pageSize, 10);
            int offset = (page - 1) * pageSize;

            const string baseQuery = @"
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

            var whereClauses = new List<string>();
            var sumaClauses = new List<string>();
            var parameters = new List<SqlParameter>();
            var parameterCounters = new Dictionary<string, int>();

            BuildFilters(request, whereClauses, parameters, parameterCounters);

            foreach (var suma in request.Selects)
            {
                if (!string.IsNullOrWhiteSpace(suma.Key))
                    sumaClauses.Add(suma.Key);
            }

            var condicionesAgrupadas = AgruparCondiciones(whereClauses);
            string whereQuery = condicionesAgrupadas.Any() ? $"WHERE {string.Join(" AND ", condicionesAgrupadas)}" : "";
            string sumaQuery = sumaClauses.Any() ? string.Join(", ", sumaClauses) : "";

            string dataQuery;
            string countQuery;

            if (sum)
            {
                dataQuery = $@"
                    WITH SumData AS (
                        SELECT 
                            {(string.IsNullOrEmpty(sumaQuery) ? "" : $"{sumaQuery},")}
                            SUM(Cantidad) AS Cantidad,
                            SUM(TotalImporte) AS TotalImporte
                        {baseQuery}
                        {whereQuery}
                        {(string.IsNullOrEmpty(sumaQuery) ? "" : $"GROUP BY {sumaQuery}")}
                    )
                    SELECT * FROM SumData
                    ORDER BY TotalImporte DESC
                    OFFSET @Offset ROWS FETCH NEXT @PageSize ROWS ONLY";

                countQuery = $"SELECT COUNT(DISTINCT {GetDistinctColumns(sumaQuery)}) AS TotalRegistros {baseQuery} {whereQuery}";
            }
            else if (distinct)
            {
                dataQuery = $@"
                    SELECT DISTINCT {(string.IsNullOrEmpty(sumaQuery) ? "*" : sumaQuery)}
                    {baseQuery}
                    {whereQuery}
                    ORDER BY {(string.IsNullOrEmpty(sumaQuery) ? "Articulo" : sumaQuery)} ASC
                    OFFSET @Offset ROWS FETCH NEXT @PageSize ROWS ONLY";

                countQuery = string.IsNullOrEmpty(sumaQuery)
                    ? $"SELECT COUNT(*) AS TotalRegistros FROM (SELECT DISTINCT * {baseQuery} {whereQuery}) AS Subquery"
                    : $"SELECT COUNT(*) AS TotalRegistros FROM (SELECT DISTINCT {sumaQuery} {baseQuery} {whereQuery}) AS Subquery";
            }
            else
            {
                dataQuery = $@"
                    SELECT 
                        {(string.IsNullOrEmpty(sumaQuery) ? "*" : sumaQuery)}
                    {baseQuery}
                    {whereQuery}
                    ORDER BY Articulo ASC
                    OFFSET @Offset ROWS FETCH NEXT @PageSize ROWS ONLY";

                countQuery = $"SELECT COUNT(*) AS TotalRegistros {baseQuery} {whereQuery}";
            }

            string combinedQuery = $"{countQuery}; {dataQuery}";

            parameters.Add(new SqlParameter("@Offset", offset));
            parameters.Add(new SqlParameter("@PageSize", pageSize));

            try
            {
                await using var connection = await OpenConnectionAsync();
                await using var command = new SqlCommand(combinedQuery, connection)
                {
                    CommandTimeout = 120
                };
                command.Parameters.AddRange(parameters.ToArray());

                await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess);

                int totalRecords = 0;
                if (await reader.ReadAsync())
                {
                    totalRecords = Convert.ToInt32(reader["TotalRegistros"]);
                }

                await reader.NextResultAsync();

                var results = new List<Dictionary<string, object>>();
                while (await reader.ReadAsync())
                {
                    var row = new Dictionary<string, object>(reader.FieldCount);
                    object[] values = new object[reader.FieldCount];
                    reader.GetValues(values);
                    for (int i = 0; i < reader.FieldCount; i++)
                    {
                        row[reader.GetName(i)] = values[i];
                    }
                    results.Add(row);
                }

                return Ok(new
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
                return HandleException(ex, combinedQuery);
            }
        }
        // Métodos compartidos
        private string GetDistinctColumns(string columns)
        {
            if (string.IsNullOrEmpty(columns)) return "ID";
            return columns.Split(',').First().Trim();
        }

        public void BuildFilters(ReporteriaRequest request, List<string> whereClauses,
            List<SqlParameter> parameters, Dictionary<string, int> parameterCounters)
        {
            var fechaEmisionParams = request.Filtros.Where(f => f.Key == "FechaEmision").ToList();
            bool fechaRangeProcessed = false;

            if (fechaEmisionParams.Count == 2)
            {
                var minFecha = fechaEmisionParams.FirstOrDefault(f => f.Operator == ">=");
                var maxFecha = fechaEmisionParams.FirstOrDefault(f => f.Operator == "<=");

                if (minFecha != null && maxFecha != null)
                {
                    whereClauses.Add("FechaEmision BETWEEN @FechaEmisionMin AND @FechaEmisionMax");
                    parameters.Add(new SqlParameter("@FechaEmisionMin", DateTime.Parse(minFecha.Value)));
                    parameters.Add(new SqlParameter("@FechaEmisionMax", DateTime.Parse(maxFecha.Value)));
                    fechaRangeProcessed = true;
                }
            }

            foreach (var filter in request.Filtros)
            {
                string operatorClause = filter.Operator?.ToLower() switch
                {
                    "like" => "LIKE",
                    "=" => "=",
                    ">=" => ">=",
                    "<=" => "<=",
                    ">" => ">",
                    "<" => "<",
                    "<>" => "<>",
                    _ => "LIKE"
                };

                if (fechaRangeProcessed && filter.Key == "FechaEmision") continue;

                if (!string.IsNullOrWhiteSpace(filter.Value))
                {
                    var column = filter.Key;
                    if (!parameterCounters.ContainsKey(column))
                        parameterCounters[column] = 0;
                    else
                        parameterCounters[column]++;

                    var paramName = $"@{column}_{parameterCounters[column]}";
                    whereClauses.Add($"{column} {operatorClause} {paramName}");

                    parameters.Add(new SqlParameter(paramName, operatorClause == "LIKE" ? $"%{filter.Value}%" : filter.Value));
                }
            }
        }

        public List<string> AgruparCondiciones(List<string> whereClauses)
        {
            var dict = new Dictionary<string, List<string>>();

            foreach (var clause in whereClauses)
            {
                var key = clause.Split(' ', 2)[0];
                if (!dict.ContainsKey(key))
                    dict[key] = new List<string>();
                dict[key].Add(clause);
            }

            return dict.Select(kvp =>
                kvp.Value.Count > 1
                    ? $"({string.Join(" OR ", kvp.Value)})"
                    : kvp.Value.First()
            ).ToList();
        }

        public async Task<string> GuardarArchivo(IFormFile archivo)
        {
            var uploadsFolder = Path.Combine(Directory.GetCurrentDirectory(), "uploads/listas");
            Directory.CreateDirectory(uploadsFolder);

            var uniqueFileName = $"{Guid.NewGuid()}{Path.GetExtension(archivo.FileName)}";
            var filePath = Path.Combine(uploadsFolder, uniqueFileName);

            await using (var stream = new FileStream(filePath, FileMode.Create))
            {
                await archivo.CopyToAsync(stream);
            }

            return filePath;
        }

    }
}