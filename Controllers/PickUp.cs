using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Caching.Memory;
using System.Data;

namespace MyApiProject.Controllers
{
    public partial class PickUp : BaseController
    {
        private readonly IMemoryCache _memoryCache;

        public PickUp(IConfiguration configuration, IMemoryCache memoryCache) : base(configuration)
        {
            _memoryCache = memoryCache;
        }

        [HttpGet("api/v1/pick-up/lista-precios")]
        public async Task<IActionResult> ObtenerCompras(
            [FromQuery] string? filtro = "",
            [FromQuery] string? id = "",
            [FromQuery] string listaPrecio = "(Precio Lista)",
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 10)
        {

            page = Math.Max(page, 1);
            pageSize = Math.Max(pageSize, 10);
            int offset = (page - 1) * pageSize;

            const string baseQuery = @"
                    FROM CB cb
                    INNER JOIN Art art ON cb.Cuenta = art.Articulo
                    INNER JOIN ListaPreciosDUnidad lpu
                        ON art.Articulo = lpu.Articulo
                        AND cb.Unidad = lpu.Unidad
                        AND lpu.Lista = @ListaPrecio
                    INNER JOIN ArtUnidad au
                        ON art.Articulo = au.Articulo
                        AND cb.Unidad = au.Unidad";
            var parameters = new List<SqlParameter>();

            string dataQuery;
            string countQuery;

            if (id != string.Empty)
            {
                Console.WriteLine("ID no es nulo, se busca por ID.");
                // Búsqueda por ID (corregido)
                dataQuery = $@"
                    WITH Resultados AS (
                        SELECT  
                        ROW_NUMBER() OVER(ORDER BY art.Descripcion1 DESC) AS ID,
                            cb.Codigo,
                            cb.Cuenta,
                            art.Grupo,
                            art.Descripcion1 AS Nombre,
                            art.Unidad,
                            lpu.Precio AS PrecioRegular,
                            au.Unidad AS UnidadFactor,
                            au.Factor
                        {baseQuery}
                    )
                    SELECT *
                    FROM Resultados
                    WHERE ID = @ID";

                countQuery = $@"
                    SELECT COUNT(*) AS TotalRegistros
                    {baseQuery}";
            }
            else if (string.IsNullOrEmpty(filtro))
            {
                Console.WriteLine("Filtro vacío, se busca sin filtro.");
                // Query sin filtro (optimizado)
                dataQuery = $@"
                    SELECT 
                    ROW_NUMBER() OVER(ORDER BY art.Descripcion1 DESC) AS ID,
                        cb.Codigo,
                        cb.Cuenta,
                        art.Grupo,
                        art.Descripcion1 AS Nombre,
                        lpu.Unidad,
                        lpu.Precio AS PrecioRegular,
                        au.Unidad AS UnidadFactor,
                        au.Factor
                    {baseQuery}
                    ORDER BY art.Descripcion1 DESC
                    OFFSET @Offset ROWS FETCH NEXT @PageSize ROWS ONLY";

                countQuery = $@"
                    SELECT COUNT(*) AS TotalRegistros
                    {baseQuery}";
            }
            else
            {
                Console.Write("Filtro no vacío, se busca por filtro.");
                // Búsqueda general (optimizado)
                dataQuery = $@"
                    SELECT  
                    ROW_NUMBER() OVER(ORDER BY art.Descripcion1 DESC) AS ID,
                        cb.Codigo,
                        cb.Cuenta,
                        art.Grupo,
                        art.Descripcion1 AS Nombre,
                        lpu.Unidad,
                        lpu.Precio AS PrecioRegular,
                        au.Unidad AS UnidadFactor,
                        au.Factor
                    {baseQuery}
                    WHERE 
                        cb.Codigo = @Filtro
                        OR art.Articulo = @Filtro
                        OR art.Descripcion1 LIKE '%' + @Filtro + '%'
                    ORDER BY art.Descripcion1 DESC
                    OFFSET @Offset ROWS FETCH NEXT @PageSize ROWS ONLY";

                countQuery = $@"
                    SELECT COUNT(*) AS TotalRegistros
                    {baseQuery}";
            }


            string combinedQuery = $"{countQuery}; {dataQuery}";

            parameters.Add(new SqlParameter("@Filtro", filtro ?? string.Empty));
            parameters.Add(new SqlParameter("@ID", id ?? string.Empty));
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
                    FROM OfertaD od
                    INNER JOIN Oferta o ON od.ID = o.ID
                    WHERE 
                        od.Articulo IN (SELECT value FROM STRING_SPLIT(@Cuentas, ','))
                        AND o.FechaD < GETDATE() 
                        AND o.FechaA > GETDATE()"; // Tu query de ofertas

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
    }
}