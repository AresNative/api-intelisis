using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Caching.Memory;
using System.Data;

namespace MyApiProject.Controllers
{
    public partial class Oferta : BaseController
    {

        private readonly IMemoryCache _memoryCache;
        public Oferta(IConfiguration configuration, IMemoryCache memoryCache) : base(configuration)
        {
            _memoryCache = memoryCache;
        }

        [HttpPost("api/ofertas")]
        public async Task<IActionResult> RegistrarOferta([FromBody] OfertaRequestDTO request)
        {
            SqlTransaction transaction = null;
            try
            {
                using (var connection = await OpenConnectionAsync())
                {
                    transaction = connection.BeginTransaction();

                    // 1. Validar existencia de artículos
                    var articulosInvalidos = new List<string>();
                    foreach (var articulo in request.Details.Select(d => d.Articulo).Distinct())
                    {
                        using (var cmd = new SqlCommand(
                            "SELECT COUNT(1) FROM Art WHERE Articulo = @Articulo",
                            connection, transaction))
                        {
                            cmd.Parameters.AddWithValue("@Articulo", articulo);
                            var exists = (int)await cmd.ExecuteScalarAsync() == 1;
                            if (!exists) articulosInvalidos.Add(articulo);
                        }
                    }

                    if (articulosInvalidos.Any())
                    {
                        return BadRequest($"Artículos no encontrados: {string.Join(", ", articulosInvalidos)}");
                    }

                    // 2. Insertar cabecera
                    int headerId;
                    using (var cmd = new SqlCommand(
                        @"INSERT INTO Oferta (
                            Mov, FechaEmision, Concepto, Usuario, MontoMinimo, Moneda, Estatus,
                            Sucursal, FechaRegistro, UltimoCambio
                          ) VALUES (
                            @Mov, GETDATE(), @Concepto, @Usuario, @MontoMinimo, @Moneda, 'Activo',
                            0, GETDATE(), GETDATE()
                          );
                          SELECT SCOPE_IDENTITY();",
                        connection, transaction))
                    {
                        cmd.Parameters.AddWithValue("@Mov", request.Header.Mov);
                        cmd.Parameters.AddWithValue("@Concepto", request.Header.Concepto);
                        cmd.Parameters.AddWithValue("@Usuario", request.Header.Usuario);
                        cmd.Parameters.AddWithValue("@Moneda", request.Header.Moneda);

                        if (request.Header.MontoMinimo.HasValue)
                            cmd.Parameters.AddWithValue("@MontoMinimo", request.Header.MontoMinimo.Value);
                        else
                            cmd.Parameters.AddWithValue("@MontoMinimo", DBNull.Value);

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
                                @EsObsequio, 'PZA'
                              )",
                            connection, transaction))
                        {
                            cmd.Parameters.AddWithValue("@HeaderId", headerId);
                            cmd.Parameters.AddWithValue("@Renglon", renglon);
                            cmd.Parameters.AddWithValue("@Articulo", detalle.Articulo);
                            cmd.Parameters.AddWithValue("@Cantidad", detalle.Cantidad);
                            cmd.Parameters.AddWithValue("@EsObsequio", detalle.EsObsequio);

                            // Manejar campos opcionales
                            if (detalle.Porcentaje.HasValue && !detalle.EsObsequio)
                                cmd.Parameters.AddWithValue("@Porcentaje", detalle.Porcentaje.Value);
                            else
                                cmd.Parameters.AddWithValue("@Porcentaje", DBNull.Value);

                            if (detalle.Precio.HasValue && !detalle.EsObsequio)
                                cmd.Parameters.AddWithValue("@Precio", detalle.Precio.Value);
                            else
                                cmd.Parameters.AddWithValue("@Precio", DBNull.Value);

                            await cmd.ExecuteNonQueryAsync();
                        }
                        renglon += 2048;
                    }

                    transaction.Commit();
                    return Ok(new { OfertaID = headerId });
                }
            }
            catch (Exception ex)
            {
                try
                {
                    transaction?.Rollback();
                    return HandleException(ex);
                }
                catch (Exception rollbackEx)
                {
                    return HandleException(rollbackEx);
                }
            }
        }

    }
}