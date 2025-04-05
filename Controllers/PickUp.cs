using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using System.ComponentModel.DataAnnotations;

namespace MyApiProject.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class ArticulosController : BaseController
    {
        private const int MaxPageSize = 100;

        public ArticulosController(IConfiguration configuration) : base(configuration) { }

        [HttpGet]
        public async Task<IActionResult> GetArticulos(
            [FromQuery] string? filtro = "",
            [FromQuery][Range(1, int.MaxValue)] int page = 1,
            [FromQuery][Range(1, MaxPageSize)] int pageSize = 50,
            [FromQuery] string listaPrecio = "(Precio Lista)")
        {
            try
            {
                var (articulos, totalCount) = await GetArticulosData(filtro ?? "", page, pageSize, listaPrecio);
                var ofertas = await GetOfertasData(articulos);

                return Ok(new
                {
                    Articulos = articulos.Select(a => MapConOfertas(a, ofertas)),
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

        private ArticuloResponse MapConOfertas(ArticuloDto articulo, List<OfertaDto> ofertas)
        {
            var oferta = ofertas.FirstOrDefault(o => o.Articulo == articulo.Cuenta);
            return new ArticuloResponse
            {
                Codigo = articulo.Codigo,
                Cuenta = articulo.Cuenta,
                Nombre = articulo.Nombre,
                Unidad = articulo.Unidad,
                PrecioRegular = articulo.PrecioRegular,
                PrecioFinal = articulo.PrecioRegular,
                PrecioOferta = oferta?.Precio,
                Factor = articulo.Factor,
                UltimoCambio = articulo.UltimoCambio,
                TieneOferta = oferta != null
            };
        }

        private async Task<(List<ArticuloDto> articulos, int totalCount)> GetArticulosData(
            string filtro, int page, int pageSize, string listaPrecio)
        {
            var articulos = new List<ArticuloDto>();
            int totalCount = 0;

            string query = @"
                WITH Paginado AS (
                    SELECT 
                        cb.Codigo,
                        cb.Cuenta,
                        art.Descripcion1 AS Nombre,
                        lpu.Unidad,
                        lpu.Precio AS PrecioRegular,
                        au.Factor,
                        art.UltimoCambio,
                        ROW_NUMBER() OVER (ORDER BY art.UltimoCambio DESC, cb.Codigo DESC) AS RowNum,
                        COUNT(*) OVER () AS TotalCount
                    FROM CB cb
                    INNER JOIN Art art ON cb.Cuenta = art.Articulo
                    INNER JOIN ListaPreciosDUnidad lpu 
                        ON art.Articulo = lpu.Articulo
                        AND cb.Unidad = lpu.Unidad
                        AND lpu.Lista = @ListaPrecio
                    INNER JOIN ArtUnidad au 
                        ON art.Articulo = au.Articulo
                        AND cb.Unidad = au.Unidad
                    WHERE 
                        @Filtro = ''
                        OR cb.Codigo = @Filtro
                        OR art.Articulo = @Filtro
                        OR art.Descripcion1 LIKE '%' + @Filtro + '%'
                )
                SELECT * 
                FROM Paginado
                WHERE RowNum BETWEEN @StartRow AND @EndRow;";

            int startRow = ((page - 1) * pageSize) + 1;
            int endRow = page * pageSize;

            using var connection = await OpenConnectionAsync();
            using var cmd = new SqlCommand(query, connection);

            cmd.Parameters.AddWithValue("@Filtro", filtro);
            cmd.Parameters.AddWithValue("@ListaPrecio", listaPrecio);
            cmd.Parameters.AddWithValue("@StartRow", startRow);
            cmd.Parameters.AddWithValue("@EndRow", endRow);

            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                if (totalCount == 0) totalCount = Convert.ToInt32(reader["TotalCount"]);

                articulos.Add(new ArticuloDto
                {
                    Codigo = reader["Codigo"].ToString(),
                    Cuenta = reader["Cuenta"].ToString(),
                    Nombre = reader["Nombre"].ToString(),
                    Unidad = reader["Unidad"].ToString(),
                    PrecioRegular = reader["PrecioRegular"] as decimal? ?? 0m, // Manejo de NULL
                    Factor = reader["Factor"]?.ToString() ?? string.Empty,
                    UltimoCambio = reader["UltimoCambio"]?.ToString() ?? string.Empty
                });
            }

            return (articulos, totalCount);
        }

        private async Task<List<OfertaDto>> GetOfertasData(List<ArticuloDto> articulos)
        {
            var ofertas = new List<OfertaDto>();
            var cuentas = articulos.Select(a => a.Cuenta).Distinct().ToList();
            if (!cuentas.Any()) return ofertas;

            string query = @"
                SELECT 
                    od.Articulo,
                    od.Precio,
                    o.FechaD,
                    o.FechaA
                FROM OfertaD od
                INNER JOIN Oferta o ON od.ID = o.ID
                WHERE 
                    od.Articulo IN (SELECT value FROM STRING_SPLIT(@Cuentas, ','))
                    AND o.FechaD < GETDATE() 
                    AND o.FechaA > GETDATE();";

            using var connection = await OpenConnectionAsync();
            using var cmd = new SqlCommand(query, connection);
            cmd.Parameters.AddWithValue("@Cuentas", string.Join(",", cuentas));

            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                // En el método donde se mapean las ofertas
                ofertas.Add(new OfertaDto
                {
                    Articulo = reader["Articulo"]?.ToString() ?? string.Empty,
                    Precio = reader["Precio"]?.ToString() ?? string.Empty,
                    FechaDesde = reader["FechaD"] as DateTime? ?? DateTime.MinValue,
                    FechaHasta = reader["FechaA"] as DateTime? ?? DateTime.MinValue
                });

            }

            return ofertas;
        }
    }

    public class ArticuloDto
    {
        public string Codigo { get; set; } = string.Empty;
        public string Cuenta { get; set; } = string.Empty;
        public string Nombre { get; set; } = string.Empty;
        public string Unidad { get; set; } = string.Empty;
        public decimal PrecioRegular { get; set; }
        public string Factor { get; set; } = string.Empty;
        public string UltimoCambio { get; set; } = string.Empty;
    }

    public class ArticuloResponse
    {
        public string Codigo { get; set; }
        public string Cuenta { get; set; }
        public string Nombre { get; set; }
        public string Unidad { get; set; }
        public decimal PrecioRegular { get; set; }
        public decimal PrecioFinal { get; set; }
        public string PrecioOferta { get; set; }
        public string Factor { get; set; }
        public string UltimoCambio { get; set; }
        public bool TieneOferta { get; set; }
    }
}