using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Caching.Memory;
using System.ComponentModel.DataAnnotations;

namespace MyApiProject.Controllers.pickUp
{
    [ApiExplorerSettings(GroupName = "pickUp")]
    [Route("api/v1/precios")]
    [ApiController]
    public partial class PreciosController : BaseController
    {
        private const int MaxPageSize = 100;
        private readonly IMemoryCache _memoryCache;

        public PreciosController(IConfiguration configuration, IMemoryCache memoryCache) : base(configuration, memoryCache)
        {
            _memoryCache = memoryCache;
        }

        // ✅ Consulta precios (sin ID)
        [HttpGet]
        public async Task<IActionResult> GetPrecios(
            [FromQuery][Required(ErrorMessage = "El parámetro de búsqueda es obligatorio")] string filtro,
            [FromQuery][Range(1, int.MaxValue)] int page = 1,
            [FromQuery][Range(1, MaxPageSize)] int pageSize = 10,
            [FromQuery] string listaPrecio = "(PRECIO 3)") // Nuevo parámetro
        {
            try
            {
                var (precios, totalCount) = await GetPreciosData(filtro, page, pageSize, listaPrecio);
                var ofertas = await GetOfertasData(precios);

                return Ok(new
                {
                    Precios = precios,
                    Ofertas = ofertas,
                    Paginacion = new
                    {
                        PaginaActual = page,
                        TamanoPagina = pageSize,
                        TotalRegistros = totalCount,
                        TotalPaginas = (int)Math.Ceiling((double)totalCount / pageSize)
                    }
                });
            }
            catch (Exception ex)
            {
                return HandleException(ex);
            }
        }

        private async Task<(List<PrecioDto> precios, int totalCount)> GetPreciosData(
            string filtro,
            int page,
            int pageSize,
            string listaPrecio) // Recibe el parámetro
        {
            var precios = new List<PrecioDto>();
            int totalCount = 0;

            string queryPrecios = @"
                WITH Paginado AS (
                    SELECT 
                        CB.Codigo, 
                        CB.Cuenta,
                        Art.Descripcion1 as Nombre,
                        ListaPreciosDUnidad.Unidad,
                        ListaPreciosDUnidad.Precio,
                        ArtUnidad.Factor,
                        Art.UltimoCambio,
                        ROW_NUMBER() OVER (ORDER BY Art.UltimoCambio DESC) AS RowNum,
                        COUNT(*) OVER () AS TotalCount
                    FROM CB
                    INNER JOIN Art ON CB.Cuenta = Art.Articulo 
                    INNER JOIN ListaPreciosDUnidad ON CB.Cuenta = ListaPreciosDUnidad.Articulo 
                    INNER JOIN ArtUnidad ON CB.Cuenta = ArtUnidad.Articulo 
                    WHERE 
                        ListaPreciosDUnidad.Lista = @ListaPrecio
                        --AND CB.Unidad = ListaPreciosDUnidad.UNIDAD 
                        --AND CB.Unidad = ArtUnidad.Unidad 
                        AND (CB.Codigo = @Filtro 
                             OR Art.Articulo = @Filtro 
                             OR Art.Descripcion1 LIKE '%' + @Filtro + '%')
                )
                SELECT * 
                FROM Paginado
                WHERE RowNum BETWEEN @StartRow AND @EndRow;
            ";
            /* 7503029889708 */
            int startRow = ((page - 1) * pageSize) + 1;
            int endRow = page * pageSize;

            await using var connection = await OpenConnectionAsync();

            await using (var command = new SqlCommand(queryPrecios, connection))
            {
                command.Parameters.AddWithValue("@Filtro", filtro);
                command.Parameters.AddWithValue("@ListaPrecio", listaPrecio); // Nuevo parámetro
                command.Parameters.AddWithValue("@StartRow", startRow);
                command.Parameters.AddWithValue("@EndRow", endRow);

                await using var reader = await command.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    if (totalCount == 0)
                        totalCount = Convert.ToInt32(reader["TotalCount"]);

                    precios.Add(new PrecioDto
                    {
                        Codigo = reader["Codigo"]?.ToString(),
                        Cuenta = reader["Cuenta"]?.ToString(),
                        Nombre = reader["Nombre"]?.ToString(),
                        Unidad = reader["Unidad"]?.ToString(),
                        Precio = reader["Precio"] as decimal? ?? 0,
                        Factor = reader["Factor"]?.ToString(),
                        UltimoCambio = reader["UltimoCambio"] as DateTime? ?? DateTime.MinValue
                    });
                }
            }

            return (precios, totalCount);
        }

        private async Task<List<OfertaDto>> GetOfertasData(List<PrecioDto> precios)
        {
            var ofertas = new List<OfertaDto>();
            var articulos = precios.Select(p => p.Cuenta).Distinct().ToList();

            if (articulos.Count == 0) return ofertas;

            string queryOfertas = @"
                SELECT 
                    OfertaD.Articulo,
                    OfertaD.Precio,
                    Oferta.FechaD,
                    Oferta.FechaA
                FROM OfertaD 
                INNER JOIN Oferta ON OfertaD.ID = Oferta.ID
                WHERE OfertaD.Articulo IN (SELECT value FROM STRING_SPLIT(@Articulos, ','))
                    AND Oferta.FechaD < GETDATE() 
                    AND Oferta.FechaA > GETDATE();";

            await using var connection = await OpenConnectionAsync();

            await using (var command = new SqlCommand(queryOfertas, connection))
            {
                command.Parameters.AddWithValue("@Articulos", string.Join(",", articulos));

                await using var reader = await command.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    ofertas.Add(new OfertaDto
                    {
                        Articulo = reader["Articulo"]?.ToString(),
                        Precio = reader["Precio"]?.ToString(),
                        FechaDesde = reader["FechaD"] as DateTime? ?? DateTime.MinValue,
                        FechaHasta = reader["FechaA"] as DateTime? ?? DateTime.MinValue
                    });
                }
            }

            return ofertas;
        }
    }

    public class PrecioDto
    {
        public string? Codigo { get; set; }
        public string? Cuenta { get; set; }
        public string? Nombre { get; set; }
        public string? Unidad { get; set; }
        public decimal? Precio { get; set; }
        public string? Factor { get; set; }
        public DateTime UltimoCambio { get; set; }
    }

    public class OfertaDto
    {
        public string? Articulo { get; set; }
        public string? Precio { get; set; }
        public DateTime FechaDesde { get; set; }
        public DateTime FechaHasta { get; set; }
    }
}
