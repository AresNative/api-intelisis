// Controllers/GlosariosController.cs
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using MyApiProject.Services;
using System.Text.RegularExpressions;

namespace MyApiProject.Controllers
{
    [ApiController]
    [Route("api/v1/glosarios")]
    public class GlosariosController : ControllerBase
    {
        private readonly IConfiguration _cfg;
        private readonly IAIService _ai;

        public GlosariosController(IConfiguration cfg, IAIService ai)
        {
            _cfg = cfg;
            _ai = ai;
        }

        [HttpGet("{tableName}")]
        public async Task<IActionResult> ObtenerGlosario(string tableName)
        {
            if (!Regex.IsMatch(tableName, @"^[A-Za-z0-9_]+$"))
                return BadRequest("Nombre de tabla no válido.");

            try
            {
                var glosario = new List<Dictionary<string, object>>();
                var connStr = _cfg.GetConnectionString("DefaultConnection")
                               ?? throw new InvalidOperationException("DefaultConnection no encontrada");

                await using var conn = new SqlConnection(connStr);
                await conn.OpenAsync();

                // Verificar existencia de tabla
                await using (var existsCmd = conn.CreateCommand())
                {
                    existsCmd.CommandText = @"
                        SELECT 1 FROM INFORMATION_SCHEMA.TABLES 
                        WHERE TABLE_NAME = @tbl";
                    existsCmd.Parameters.AddWithValue("@tbl", tableName);
                    if (await existsCmd.ExecuteScalarAsync() == null)
                        return NotFound($"La tabla '{tableName}' no existe.");
                }

                // Leer esquema
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = $"SELECT TOP 1 * FROM [{tableName}]";
                await using var reader = await cmd.ExecuteReaderAsync();
                var schema = reader.GetSchemaTable()!;

                for (int i = 0; i < reader.FieldCount; i++)
                {
                    var colName = reader.GetName(i);
                    var meta = schema.Rows[i];
                    var descripcion = await _ai.DescribeColumnAsync(tableName, colName);

                    glosario.Add(new Dictionary<string, object>
                    {
                        ["Nombre"] = colName,
                        ["TipoDato"] = reader.GetDataTypeName(i),
                        ["Tamaño"] = meta["ColumnSize"],
                        ["EsNulo"] = meta["AllowDBNull"],
                        ["Descripcion"] = descripcion
                    });
                }

                return Ok(glosario);
            }
            catch (Exception ex)
            {
                return StatusCode(500, new
                {
                    Mensaje = "Error al generar el glosario dinámico.",
                    Detalle = ex.Message
                });
            }
        }
    }
}
