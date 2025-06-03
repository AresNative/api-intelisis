using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Caching.Memory;
using System.Data;

namespace MyApiProject.Controllers
{
    public partial class Oferta : BaseController
    {
        private readonly IMemoryCache _memoryCache;
        public Oferta(IConfiguration configuration, IMemoryCache memoryCache) : base(configuration, memoryCache)
        {
            _memoryCache = memoryCache;
        }

        [HttpPost("api/ofertas")]
        public async Task<IActionResult> RegistrarOferta([FromBody] OfertaRequestDTO request)
        {
            if (request?.Header == null || request.Details == null || !request.Details.Any())
            {
                return BadRequest("Solicitud inválida: faltan cabecera o detalles");
            }

            SqlTransaction transaction = null;
            SqlConnection connection = null;
            try
            {
                connection = await OpenConnectionAsync();
                transaction = connection.BeginTransaction();

                // 1. Validar artículos en lote
                var articulos = request.Details.Select(d => d.Articulo).Distinct().ToList();
                var articulosInvalidos = new List<string>();

                if (articulos.Any())
                {
                    var inClause = string.Join(",", articulos.Select((a, i) => $"@Art{i}"));
                    var query = $"SELECT Articulo FROM Art WHERE Articulo IN ({inClause})";

                    using (var cmd = new SqlCommand(query, connection, transaction))
                    {
                        for (int i = 0; i < articulos.Count; i++)
                        {
                            cmd.Parameters.AddWithValue($"@Art{i}", articulos[i]);
                        }

                        var articulosValidos = new HashSet<string>();
                        using (var reader = await cmd.ExecuteReaderAsync())
                        {
                            while (await reader.ReadAsync())
                            {
                                articulosValidos.Add(reader["Articulo"].ToString());
                            }
                        }
                        articulosInvalidos = articulos.Where(a => !articulosValidos.Contains(a)).ToList();
                    }
                }

                if (articulosInvalidos.Any())
                {
                    return BadRequest($"Artículos no encontrados: {string.Join(", ", articulosInvalidos)}");
                }

                // 2. Insertar cabecera (SOLUCIÓN CORREGIDA)
                int headerId;
                using (var cmd = new SqlCommand(
                    @"INSERT INTO Oferta (
                        Mov, FechaEmision, Concepto, Usuario, MontoMinimo, Moneda, Estatus,
                        Sucursal, FechaRegistro, UltimoCambio,Empresa
                      ) VALUES (
                        @Mov, GETDATE(), @Concepto, @Usuario, @MontoMinimo, @Moneda, 'Activo',
                        0, GETDATE(), GETDATE(), 'SMM'
                      );
                      SELECT SCOPE_IDENTITY();",  // Cambiado a SCOPE_IDENTITY()
                    connection, transaction))
                {
                    cmd.Parameters.AddWithValue("@Mov", request.Header.Mov);
                    cmd.Parameters.AddWithValue("@Concepto", request.Header.Concepto ?? (object)DBNull.Value);
                    cmd.Parameters.AddWithValue("@Usuario", request.Header.Usuario ?? (object)DBNull.Value);
                    cmd.Parameters.AddWithValue("@Moneda", request.Header.Moneda ?? (object)DBNull.Value);
                    cmd.Parameters.AddWithValue("@MontoMinimo", request.Header.MontoMinimo ?? (object)DBNull.Value);

                    headerId = Convert.ToInt32(await cmd.ExecuteScalarAsync());
                }

                // 3. Insertar detalles
                int renglon = 2048;
                foreach (var detalle in request.Details)
                {
                    using (var cmd = new SqlCommand(
                        @"INSERT INTO OfertaD (
                            ID, Renglon, Articulo, Cantidad, Porcentaje, Precio, Obsequio, Unidad
                          ) VALUES (
                            @HeaderId, @Renglon, @Articulo, @Cantidad, @Porcentaje, @Precio, 
                            @Obsequio, 'PZA'
                          )",
                        connection, transaction))
                    {
                        cmd.Parameters.AddWithValue("@HeaderId", headerId);
                        cmd.Parameters.AddWithValue("@Renglon", renglon);
                        cmd.Parameters.AddWithValue("@Articulo", detalle.Articulo);
                        cmd.Parameters.AddWithValue("@Cantidad", detalle.Cantidad);
                        cmd.Parameters.AddWithValue("@Obsequio", detalle.EsObsequio);

                        cmd.Parameters.AddWithValue("@Porcentaje",
                            detalle.EsObsequio ? (object)DBNull.Value : detalle.Porcentaje ?? (object)DBNull.Value);

                        cmd.Parameters.AddWithValue("@Precio",
                            detalle.EsObsequio ? (object)DBNull.Value : detalle.Precio ?? (object)DBNull.Value);

                        await cmd.ExecuteNonQueryAsync();
                    }
                    renglon += 2048;
                }

                await transaction.CommitAsync();
                return Ok(new { OfertaID = headerId });
            }
            catch (Exception ex)
            {
                try
                {
                    if (transaction != null)
                        await transaction.RollbackAsync();
                }
                catch (Exception rollbackEx)
                {
                    return HandleException(rollbackEx);
                }
                return HandleException(ex);
            }
            finally
            {
                if (connection != null)
                    await connection.CloseAsync();
            }
        }

        [HttpGet("api/ofertas/vigentes")]
        public async Task<IActionResult> ObtenerOfertasVigentes()
        {
            try
            {
                using (var connection = await OpenConnectionAsync())
                {
                    var query = @"
                SELECT 
                    o.ID, o.Mov, o.Concepto, o.FechaEmision, o.FechaD AS FechaInicio, 
                    o.FechaA AS FechaFin, o.MontoMinimo, o.Moneda,
                    d.Renglon, d.Articulo, d.Cantidad, d.Porcentaje, d.Precio, d.Obsequio
                FROM Oferta o
                LEFT JOIN OfertaD d ON o.ID = d.ID
                WHERE o.Estatus = 'Activo'
                    AND (o.FechaD IS NULL OR o.FechaD <= GETDATE())
                    AND (o.FechaA IS NULL OR o.FechaA >= GETDATE())
                ORDER BY o.ID, d.Renglon";

                    var ofertasDict = new Dictionary<int, OfertaVigenteDTO>();

                    using (var cmd = new SqlCommand(query, connection))
                    using (var reader = await cmd.ExecuteReaderAsync())
                    {
                        while (await reader.ReadAsync())
                        {
                            var id = reader.GetInt32(reader.GetOrdinal("ID"));

                            if (!ofertasDict.TryGetValue(id, out var oferta))
                            {
                                oferta = new OfertaVigenteDTO
                                {
                                    ID = id,
                                    Mov = reader.GetString(reader.GetOrdinal("Mov")),
                                    Concepto = reader.IsDBNull("Concepto") ? null : reader.GetString("Concepto"),
                                    FechaEmision = reader.GetDateTime("FechaEmision"),
                                    FechaInicio = reader.IsDBNull("FechaInicio") ? (DateTime?)null : reader.GetDateTime("FechaInicio"),
                                    FechaFin = reader.IsDBNull("FechaFin") ? (DateTime?)null : reader.GetDateTime("FechaFin"),
                                    MontoMinimo = reader.IsDBNull("MontoMinimo") ? (decimal?)null : reader.GetDecimal("MontoMinimo"),
                                    Moneda = reader.IsDBNull("Moneda") ? null : reader.GetString("Moneda"),
                                    Detalles = new List<OfertaDetalleVigenteDTO>()
                                };
                                ofertasDict[id] = oferta;
                            }

                            if (!reader.IsDBNull("Renglon"))
                            {
                                var detalle = new OfertaDetalleVigenteDTO
                                {
                                    Renglon = reader.GetFloat("Renglon"),
                                    Articulo = reader.GetString("Articulo"),
                                    Cantidad = reader.IsDBNull("Cantidad") ? (float?)null : (float)reader.GetDouble("Cantidad"),
                                    Porcentaje = reader.IsDBNull("Porcentaje") ? (float?)null : (float)reader.GetDouble("Porcentaje"),
                                    Precio = reader.IsDBNull("Precio") ? (float?)null : (float)reader.GetDouble("Precio"),
                                    Obsequio = reader.IsDBNull("Obsequio") ? null : reader.GetString("Obsequio")
                                };
                                oferta.Detalles.Add(detalle);
                            }
                        }
                    }

                    return Ok(ofertasDict.Values.ToList());
                }
            }
            catch (Exception ex)
            {
                return HandleException(ex);
            }
        }
        [HttpPost("api/combos")]
        public async Task<IActionResult> RegistrarCombo([FromBody] OfertaRequestDTO request)
        {
            if (request?.Header == null)
                request.Header = new OfertaHeaderDTO();

            // Forzar tipo de movimiento para combos
            request.Header.Mov = "Combo";

            // Reutilizar lógica existente de ofertas
            return await RegistrarOferta(request);
        }
        // DTOs para la respuesta
        public class OfertaVigenteDTO
        {
            public int ID { get; set; }
            public string Mov { get; set; }
            public string Concepto { get; set; }
            public DateTime FechaEmision { get; set; }
            public DateTime? FechaInicio { get; set; }
            public DateTime? FechaFin { get; set; }
            public decimal? MontoMinimo { get; set; }
            public string Moneda { get; set; }
            public List<OfertaDetalleVigenteDTO> Detalles { get; set; }
        }

        public class OfertaDetalleVigenteDTO
        {
            public float Renglon { get; set; }
            public string Articulo { get; set; }
            public float? Cantidad { get; set; }
            public float? Porcentaje { get; set; }
            public float? Precio { get; set; }
            public string Obsequio { get; set; } // "True" indica artículo obsequio
        }
    }
}