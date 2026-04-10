// Controllers/GeneralController.cs
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Caching.Memory;
using MyApiProject.Models;
using MyApiProject.Attributes;
using Microsoft.AspNetCore.SignalR;
using MyApiProject.Hubs;
using Newtonsoft.Json.Linq;

namespace MyApiProject.Controllers.general
{
    [ApiExplorerSettings(GroupName = "general")]
    [Route("api/v1")]
    [ApiController]
    public partial class GeneralController : BaseController
    {
        private readonly ILogger<GeneralController> _logger;
        private readonly IHubContext<GeneralHubs> _hubContext;

        public GeneralController(
            IConfiguration configuration,
            IMemoryCache memoryCache,
            ILogger<GeneralController> logger,
            IHubContext<GeneralHubs> hubContext)
            : base(configuration, memoryCache)
        {
            _logger = logger;
            _hubContext = hubContext;
        }

        // ── Helpers internos ──────────────────────────────────────────────────

        private async Task NotificarAsync(string evento, object payload) =>
            await _hubContext.Clients.Group("PedidosGeneral").SendAsync(evento, payload);

        private static async Task<List<Dictionary<string, object?>>> LeerFilasAsync(SqlDataReader reader)
        {
            var results = new List<Dictionary<string, object?>>();
            while (await reader.ReadAsync())
            {
                var row = new Dictionary<string, object?>();
                for (int i = 0; i < reader.FieldCount; i++)
                    row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                results.Add(row);
            }
            return results;
        }

        /// <summary>
        /// Valida tabla (contra INFORMATION_SCHEMA) y columna (regex de identificador).
        /// Retorna BadRequest listo para devolver si alguno falla, o null si todo es válido.
        /// </summary>
        private async Task<IActionResult?> ValidarTablaYColumnaAsync(string table, string? column = null)
        {
            var (tableValid, tableError) = await ValidateFromClauseAsync(table);
            if (!tableValid)
                return BadRequest(new { Message = $"Tabla inválida: {tableError}" });

            if (column != null)
            {
                var (colValid, colError) = ValidateIdentifier(column, "Columna");
                if (!colValid)
                    return BadRequest(new { Message = colError });
            }

            return null;
        }

        // ── GET: consultar por ID ─────────────────────────────────────────────

        [HttpGet("consultar/{id}")]
        [ValidateToken]
        public async Task<IActionResult> ConsultarPorId(string id, [FromQuery] string table = "general", [FromQuery] string column = "id")
        {
            var validacion = await ValidarTablaYColumnaAsync(table, column);
            if (validacion != null) return validacion;

            string cacheKey = $"general_{table}_{column}_{id}";
            if (_cache.TryGetValue(cacheKey, out List<Dictionary<string, object?>> cachedResults))
                return Ok(cachedResults);

            try
            {
                await using var connection = await OpenConnectionAsync();
                await using var command = new SqlCommand($"SELECT * FROM [{table}] WHERE [{column}] = @ID", connection);
                command.Parameters.AddWithValue("@ID", id);

                await using var reader = await command.ExecuteReaderAsync();
                var results = await LeerFilasAsync(reader);

                _cache.Set(cacheKey, results, TimeSpan.FromMinutes(5));
                await NotificarAsync("DatosActualizados", new { Tabla = table, Accion = "ConsultaID", TotalRegistros = results.Count, Timestamp = DateTime.UtcNow });
                return Ok(results);
            }
            catch (Exception ex) { return HandleException(ex, $"ConsultarPorId tabla={table} columna={column} id={id}"); }
        }

        // ── POST: consultar con filtros paginados ─────────────────────────────

        [HttpPost("consultar")]
        [ValidateToken]
        public async Task<IActionResult> ConsultarGeneral(
            [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] FiltrosRequest? request,
            [FromQuery] string fromClause = "")
        {
            request ??= new FiltrosRequest();

            if (string.IsNullOrWhiteSpace(fromClause))
                return BadRequest(new { Message = "El parámetro 'fromClause' es requerido." });

            // Validar el FROM clause antes de usarlo (regex + INFORMATION_SCHEMA)
            var (valid, error) = await ValidateFromClauseAsync(fromClause);
            if (!valid)
                return BadRequest(new { Message = $"FROM clause inválido: {error}" });

            return await ExecuteMassiveQueryAsync(request, fromClause, _logger);
        }

        // ── POST: registrar ───────────────────────────────────────────────────

        [HttpPost("register")]
        [ValidateToken]
        public async Task<IActionResult> Registrar(
            [FromBody] JObject data,
            [FromQuery] string table = "general")
        {
            if (data == null) return BadRequest(new { Message = "JSON inválido." });

            var validacion = await ValidarTablaYColumnaAsync(table);
            if (validacion != null) return validacion;

            var properties = data.Properties()
                .Where(p => !string.IsNullOrWhiteSpace(p.Name))
                .ToList();

            if (!properties.Any())
                return BadRequest(new { Message = "El JSON no contiene propiedades válidas." });

            // Validar también los nombres de columna del JSON recibido
            foreach (var prop in properties)
            {
                var (colValid, colError) = ValidateIdentifier(prop.Name, "Columna");
                if (!colValid) return BadRequest(new { Message = colError });
            }

            try
            {
                var columnNames = string.Join(", ", properties.Select(p => $"[{p.Name}]"));
                var paramNames = string.Join(", ", properties.Select(p => $"@{p.Name}"));

                var query = $@"
                    INSERT INTO [{table}] ({columnNames})
                    OUTPUT INSERTED.*
                    VALUES ({paramNames})";

                await using var connection = await OpenConnectionAsync();
                await using var command = new SqlCommand(query, connection);

                foreach (var prop in properties)
                    command.Parameters.AddWithValue($"@{prop.Name}", prop.Value?.ToObject<object>() ?? DBNull.Value);

                var insertedId = await command.ExecuteScalarAsync();

                _cache.Remove($"general_all_{table}");
                await NotificarAsync("NuevoRegistro", new { Tabla = table, Accion = "Insert", Id = insertedId, Timestamp = DateTime.UtcNow });

                return Ok(new { Message = "Registro exitoso.", Id = insertedId });
            }
            catch (Exception ex) { return HandleException(ex, $"Registrar tabla={table}"); }
        }

        // ── PUT: actualizar ───────────────────────────────────────────────────

        [HttpPut("update/{tabla}")]
        [ValidateToken]
        public async Task<IActionResult> Actualizar(string tabla, [FromBody] ActualizarRequest request)
        {
            if (request?.Data == null)
                return BadRequest(new { Message = "Datos inválidos o faltantes." });

            var validacion = await ValidarTablaYColumnaAsync(tabla);
            if (validacion != null) return validacion;

            var filtros = request.Filtros?
                .Where(f => !string.IsNullOrWhiteSpace(f.Key) && !string.IsNullOrWhiteSpace(f.Value))
                .ToList();

            if (filtros == null || !filtros.Any())
                return BadRequest(new { Message = "Se requieren filtros válidos para la actualización." });

            var whereConditions = new List<(string column, string op, string value)>();
            foreach (var f in filtros)
            {
                var op = NormalizeSimpleOperator(f.Operator);
                whereConditions.Add((f.Key, op, f.Value));
            }

            var setProperties = request.Data.Properties()
                .Where(p => !string.IsNullOrWhiteSpace(p.Name))
                .ToList();

            if (!setProperties.Any())
                return BadRequest(new { Message = "El JSON no contiene propiedades válidas para actualizar." });

            // Validar también los nombres de columna del JSON recibido
            foreach (var prop in setProperties)
            {
                var (colValid, colError) = ValidateIdentifier(prop.Name, "Columna");
                if (!colValid) return BadRequest(new { Message = colError });
            }

            var setClause = string.Join(", ", setProperties.Select(p => $"[{p.Name}] = @s_{p.Name}"));

            // Construir cláusula WHERE
            var whereParts = new List<string>();
            for (int i = 0; i < whereConditions.Count; i++)
                whereParts.Add($"[{whereConditions[i].column}] {whereConditions[i].op} @w{i}");
            var whereClause = string.Join(" AND ", whereParts);
            var updateQuery = $"UPDATE [{tabla}] SET {setClause} WHERE {whereClause}";
            var selectQuery = $"SELECT * FROM [{tabla}] WHERE {whereClause}";

            try
            {
                await using var connection = await OpenConnectionAsync();

                // ---- UPDATE (sin transacción) ----
                int rowsAffected;
                using (var updateCmd = new SqlCommand(updateQuery, connection))
                {
                    // Parámetros SET
                    foreach (var prop in setProperties)
                        updateCmd.Parameters.AddWithValue($"@s_{prop.Name}", prop.Value?.ToObject<object>() ?? DBNull.Value);

                    // Parámetros WHERE
                    for (int i = 0; i < whereConditions.Count; i++)
                    {
                        var (_, op, value) = whereConditions[i];
                        var pName = $"@w{i}";
                        var paramValue = (op == "LIKE" && !value.StartsWith("%") && !value.EndsWith("%")) ? $"%{value}%" : value;
                        var param = QB.CreateTypedParameter(pName, paramValue);
                        updateCmd.Parameters.Add(param);
                    }

                    rowsAffected = await updateCmd.ExecuteNonQueryAsync();
                } // updateCmd se cierra aquí

                if (rowsAffected == 0)
                    return NotFound(new { Message = "Registro no encontrado o no se pudo actualizar." });

                // ---- SELECT (usando la misma conexión, pero con nuevo comando) ----
                List<Dictionary<string, object?>> updatedResults;
                using (var selectCmd = new SqlCommand(selectQuery, connection))
                {
                    // Recrear parámetros WHERE
                    for (int i = 0; i < whereConditions.Count; i++)
                    {
                        var (_, op, value) = whereConditions[i];
                        var pName = $"@w{i}";
                        var paramValue = (op == "LIKE" && !value.StartsWith("%") && !value.EndsWith("%")) ? $"%{value}%" : value;
                        var param = QB.CreateTypedParameter(pName, paramValue);
                        selectCmd.Parameters.Add(param);
                    }

                    await using var reader = await selectCmd.ExecuteReaderAsync();
                    updatedResults = await LeerFilasAsync(reader);
                }

                // Limpiar caché
                foreach (var f in filtros)
                    _cache.Remove($"general_{tabla}_{f.Key}_{f.Value}");
                _cache.Remove($"general_all_{tabla}");

                // Notificar
                var notifId = string.Join("_", filtros.Select(f => $"{f.Key}_{f.Value}"));
                await NotificarAsync("RegistroActualizado", new
                {
                    Tabla = tabla,
                    Filtros = notifId,
                    Accion = "Update",
                    Timestamp = DateTime.UtcNow,
                    RegistrosAfectados = updatedResults.Count
                });

                return Ok(new { Message = "Actualización exitosa.", RegistrosAfectados = updatedResults.Count, Data = updatedResults });
            }
            catch (Exception ex)
            {
                return HandleException(ex, $"Actualizar tabla={tabla}");
            }
        }

        // ── DELETE: archivar (lógico) ─────────────────────────────────────────

        [HttpDelete("archivar/{id}")]
        [ValidateToken]
        public async Task<IActionResult> Archivar(
            string id,
            [FromQuery] string column = "id",
            [FromQuery] string table = "general")
        {
            var validacion = await ValidarTablaYColumnaAsync(table, column);
            if (validacion != null) return validacion;

            try
            {
                await using var connection = await OpenConnectionAsync();

                var originalData = await GetRowByColumnAsync(connection, table, column, id);
                if (originalData == null)
                    return NotFound(new { Message = "Registro no encontrado." });

                await using var updateCmd = new SqlCommand(
                    $"UPDATE [{table}] SET estado = 'archivado' WHERE [{column}] = @Id", connection);
                updateCmd.Parameters.AddWithValue("@Id", id);

                if (await updateCmd.ExecuteNonQueryAsync() == 0)
                    return NotFound(new { Message = "Registro no encontrado." });

                _cache.Remove($"general_{table}_{id}");
                _cache.Remove($"general_all_{table}");

                await NotificarAsync("RegistroArchivado", new
                {
                    Tabla = table,
                    RegistroId = id,
                    DatosOriginales = originalData,
                    Accion = "Archive",
                    Timestamp = DateTime.UtcNow
                });

                return Ok(new { Message = "Registro archivado exitosamente.", Data = originalData });
            }
            catch (Exception ex) { return HandleException(ex, $"Archivar tabla={table} id={id}"); }
        }

        // ── DELETE: eliminar (físico) ─────────────────────────────────────────

        [HttpDelete("delete/{id}")]
        [ValidateToken]
        public async Task<IActionResult> Eliminar(
            string id,
            [FromQuery] string column = "id",
            [FromQuery] string table = "general")
        {
            var validacion = await ValidarTablaYColumnaAsync(table, column);
            if (validacion != null) return validacion;

            try
            {
                await using var connection = await OpenConnectionAsync();

                var originalData = await GetRowByColumnAsync(connection, table, column, id);
                if (originalData == null)
                    return NotFound(new { Message = "Registro no encontrado." });

                await using var deleteCmd = new SqlCommand(
                    $"DELETE FROM [{table}] WHERE [{column}] = @Id", connection);
                deleteCmd.Parameters.AddWithValue("@Id", id);

                if (await deleteCmd.ExecuteNonQueryAsync() == 0)
                    return NotFound(new { Message = "Registro no encontrado." });

                _cache.Remove($"general_{table}_{id}");
                _cache.Remove($"general_all_{table}");

                await NotificarAsync("RegistroEliminado", new
                {
                    Tabla = table,
                    RegistroId = id,
                    DatosOriginales = originalData,
                    Accion = "Delete",
                    Timestamp = DateTime.UtcNow
                });

                return Ok(new { Message = "Registro eliminado exitosamente.", Data = originalData });
            }
            catch (Exception ex) { return HandleException(ex, $"Eliminar tabla={table} id={id}"); }
        }

        // ── Helpers privados ──────────────────────────────────────────────────

        private static async Task<Dictionary<string, object?>?> GetRowByColumnAsync(
            SqlConnection connection, string table, string column, string id)
        {
            await using var cmd = new SqlCommand(
                $"SELECT * FROM [{table}] WHERE [{column}] = @Id", connection);
            cmd.Parameters.AddWithValue("@Id", id);

            await using var reader = await cmd.ExecuteReaderAsync();
            if (!await reader.ReadAsync()) return null;

            var row = new Dictionary<string, object?>();
            for (int i = 0; i < reader.FieldCount; i++)
                row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);

            return row;
        }

        private static string NormalizeSimpleOperator(string? op) =>
            op?.ToUpperInvariant() switch
            {
                "LIKE" => "LIKE",
                ">=" => ">=",
                "<=" => "<=",
                ">" => ">",
                "<" => "<",
                "<>" => "<>",
                "!=" => "<>",
                _ => "="
            };
    }
}