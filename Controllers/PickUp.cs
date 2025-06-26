using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Caching.Memory;
using System.Data;
using MyApiProject.Models;
using System.Security.Cryptography;
using System.Text;

namespace MyApiProject.Controllers
{
    public partial class PickUp : BaseController
    {
        private readonly IMemoryCache _memoryCache;

        public class FilterRequest
        {
            public List<BusquedaParams> Filtros { get; set; } = new();
            public List<SumaParams> Selects { get; set; } = new();
            public List<OrderParams> Order { get; set; } = new();
        }

        public PickUp(IConfiguration configuration, IMemoryCache memoryCache) : base(configuration, memoryCache)
        {
            _memoryCache = memoryCache;
        }

        [HttpGet("api/v1/pick-up")]
        public async Task<IActionResult> ObtenerProductos(
            [FromQuery] string? filtro = "",
            [FromQuery] string? categoria = "",
            [FromQuery] string listaPrecio = "(Precio Lista)",
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 10)
        {
            // Construir clave de caché única
            string cacheKey = BuildCacheKey(filtro, categoria, listaPrecio, page, pageSize);

            // Verificar caché
            if (_memoryCache.TryGetValue(cacheKey, out object cachedResponse))
            {
                return Ok(cachedResponse);
            }

            page = Math.Max(page, 1);
            pageSize = Math.Max(pageSize, 10);
            int offset = (page - 1) * pageSize;

            const string baseQuery = @"
                    FROM [TC032841E].[dbo].[CB] AS cb
                    INNER JOIN [TC032841E].[dbo].[Art] AS art 
                        ON cb.Cuenta = art.Articulo
                    INNER JOIN [TC032841E].[dbo].[ListaPreciosDUnidad] AS lpu
                        ON art.Articulo = lpu.Articulo
                        AND cb.Unidad = lpu.Unidad
                        AND lpu.Lista = @ListaPrecio
                    INNER JOIN [TC032841E].[dbo].[ArtUnidad] AS au
                        ON art.Articulo = au.Articulo
                        AND lpu.Unidad = au.Unidad  
                    INNER JOIN (
                        SELECT 
                            Articulo,
	                        SUM(Inventario)  as TotalInventario
                        FROM ArtExistenciaInv 
                        WHERE 
                            Almacen =
                            CASE 
                                WHEN @ListaPrecio = '(Precio Lista)' THEN 'ALMMAYO'
                                WHEN @ListaPrecio = '(Precio 4)' THEN 'ALMGPE'
                                WHEN @ListaPrecio = '(Precio 3)' THEN 'ALMPALM'
                                WHEN @ListaPrecio = '(Precio 2)' THEN 'ALMTESTE'
                                ELSE'(Precio Lista)'
                                END
                        GROUP BY Articulo
                    ) AS inv 
                        ON art.Articulo = inv.Articulo";

            var parameters = new List<SqlParameter>();

            string dataQuery;
            string countQuery;

            if (categoria != string.Empty)
            {
                dataQuery = $@"
                    ;WITH Resultados AS (
                        SELECT  
                            ROW_NUMBER() OVER(ORDER BY art.Descripcion1) AS ID,
                            cb.Codigo,
                            cb.Cuenta,
                            art.Grupo,
                            art.Descripcion1 AS Nombre,
                            art.Unidad,
                            lpu.Precio AS PrecioRegular,
                            au.Unidad AS UnidadFactor,
                            au.Factor,
                            inv.TotalInventario
                        {baseQuery}
                    )
                    SELECT *
                    FROM Resultados
                    WHERE Grupo = @Categoria";

                countQuery = $@"
                    SELECT COUNT(*) AS TotalRegistros
                    {baseQuery}
                    WHERE art.Grupo = @Categoria";
            }
            else if (filtro != string.Empty)
            {
                // Búsqueda general (optimizado)
                dataQuery = $@"
                    SELECT  
                        cb.Codigo,
                        cb.Cuenta,
                        art.Grupo,
                        art.Descripcion1 AS Nombre,
                        lpu.Unidad,
                        lpu.Precio AS PrecioRegular,
                        au.Unidad AS UnidadFactor,
                        au.Factor,
                        art.Articulo,
                        inv.TotalInventario
                    {baseQuery}
                    WHERE 
                        cb.Codigo = @Filtro
                        OR art.Articulo = @Filtro
                        OR art.Descripcion1 LIKE '%' + @Filtro + '%'
                    ORDER BY art.Descripcion1
                    OFFSET @Offset ROWS FETCH NEXT @PageSize ROWS ONLY";

                countQuery = $@"
                    SELECT COUNT(*) AS TotalRegistros
                    {baseQuery}
                    WHERE 
                        cb.Codigo = @Filtro
                        OR art.Articulo = @Filtro
                        OR art.Descripcion1 LIKE '%' + @Filtro + '%'";
            }
            else
            {
                // Query sin filtro (optimizado)
                dataQuery = $@"
                    SELECT 
                        cb.Codigo,
                        cb.Cuenta,
                        art.Grupo,
                        art.Descripcion1 AS Nombre,
                        lpu.Unidad,
                        lpu.Precio AS PrecioRegular,
                        au.Unidad AS UnidadFactor,
                        au.Factor,
                        inv.TotalInventario
                    {baseQuery}
                    ORDER BY art.Descripcion1
                    OFFSET @Offset ROWS FETCH NEXT @PageSize ROWS ONLY";

                countQuery = $@"
                    SELECT COUNT(*) AS TotalRegistros
                    {baseQuery}";
            }

            string combinedQuery = $"{countQuery}; {dataQuery}";

            parameters.Add(new SqlParameter("@Filtro", filtro ?? string.Empty));
            parameters.Add(new SqlParameter("@Categoria", categoria ?? string.Empty));
            parameters.Add(new SqlParameter("@ListaPrecio", listaPrecio ?? string.Empty));
            parameters.Add(new SqlParameter("@Offset", offset));
            parameters.Add(new SqlParameter("@PageSize", pageSize));

            try
            {
                await using var connection = await OpenConnectionAsync();

                // Primera ejecución para countQuery y dataQuery
                List<Dictionary<string, object>> results;
                int totalRecords = 0;

                // Ejecutar consulta principal dentro de un bloque using
                using (var mainCommand = new SqlCommand(combinedQuery, connection))
                {
                    mainCommand.Parameters.AddRange(parameters.ToArray());

                    await using var reader = await mainCommand.ExecuteReaderAsync();

                    // Leer primer resultado (count)
                    if (await reader.ReadAsync())
                    {
                        totalRecords = Convert.ToInt32(reader["TotalRegistros"]);
                    }

                    // Leer segundo resultado (datos)
                    await reader.NextResultAsync();

                    results = new List<Dictionary<string, object>>();
                    while (await reader.ReadAsync())
                    {
                        var row = new Dictionary<string, object>();
                        for (int i = 0; i < reader.FieldCount; i++)
                        {
                            row[reader.GetName(i)] = reader[i];
                        }
                        results.Add(row);
                    }
                } // El reader y command se cierran aquí automáticamente

                // Consulta de ofertas solo si hay resultados
                if (results.Count > 0)
                {
                    var ofertasQuery = @"
                    SELECT 
                        od.Articulo,
                        od.Precio
                    FROM [TC032841E].[dbo].OfertaD od
                    INNER JOIN [TC032841E].[dbo].Oferta o ON od.ID = o.ID
                    WHERE 
                        od.Articulo IN (SELECT value FROM STRING_SPLIT(@Cuentas, ','))
                        AND o.FechaD < GETDATE() 
                        AND o.FechaA > GETDATE()";

                    using (var ofertasCommand = new SqlCommand(ofertasQuery, connection))
                    {
                        ofertasCommand.Parameters.Add(new SqlParameter("@Cuentas", string.Join(",", results.Select(r => r["Cuenta"]))));

                        await using var ofertasReader = await ofertasCommand.ExecuteReaderAsync();

                        var ofertasDict = new Dictionary<string, decimal>();
                        while (await ofertasReader.ReadAsync())
                        {
                            ofertasDict[ofertasReader["Articulo"].ToString()] =
                                Convert.ToDecimal(ofertasReader["Precio"]);
                        }

                        // Añadir ofertas a los resultados
                        foreach (var row in results)
                        {
                            if (ofertasDict.TryGetValue(row["Cuenta"].ToString(), out var precio))
                            {
                                row["PrecioOferta"] = precio;
                                row["TieneOferta"] = true;
                            }
                            else
                            {
                                row["TieneOferta"] = false;
                            }
                        }
                    }
                }

                var response = new
                {
                    TotalRecords = totalRecords,
                    TotalPages = (int)Math.Ceiling(totalRecords / (double)pageSize),
                    Page = page,
                    PageSize = pageSize,
                    Data = results
                };

                // Almacenar en caché
                var cacheOptions = new MemoryCacheEntryOptions()
                    .SetSlidingExpiration(TimeSpan.FromMinutes(1));
                _memoryCache.Set(cacheKey, response, cacheOptions);

                return Ok(response);
            }
            catch (Exception ex)
            {
                return HandleException(ex, combinedQuery);
            }
        }

        private string BuildCacheKey(string filtro, string categoria, string listaPrecio, int page, int pageSize)
        {
            var rawKey = $"{filtro}_{categoria}_{listaPrecio}_{page}_{pageSize}";

            using (var sha1 = SHA1.Create())
            {
                byte[] hash = sha1.ComputeHash(Encoding.UTF8.GetBytes(rawKey));
                var sb = new StringBuilder(40);
                foreach (byte b in hash)
                    sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        public void BuildFilters(FilterRequest request, List<string> whereClauses, List<SqlParameter> parameters,
            Dictionary<string, int> parameterCounters, string tableName)
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

                if (!string.IsNullOrWhiteSpace(filter.Value) && filter.Key == "Codigo")
                {
                    if (!parameterCounters.ContainsKey(filter.Key))
                        parameterCounters[filter.Key] = 0;
                    else
                        parameterCounters[filter.Key]++;

                    var paramName = $"@Codigo_{parameterCounters[filter.Key]}";
                    whereClauses.Add($"Articulo IN (SELECT Articulo FROM [TC032391E].[dbo].[{tableName}] WHERE Codigo {operatorClause} {paramName})");

                    parameters.Add(new SqlParameter(paramName, operatorClause == "LIKE" ? $"%{filter.Value}%" : filter.Value));
                }
                else if (!string.IsNullOrWhiteSpace(filter.Value))
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
                var key = clause.Split(' ', 2)[0]; // Tomamos la primera palabra como clave
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