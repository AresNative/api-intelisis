using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.AspNetCore.Authorization;
using Newtonsoft.Json.Linq;
using Microsoft.Extensions.Caching.Memory;
using MyApiProject.Models;

namespace MyApiProject.Controllers.general
{
    [ApiExplorerSettings(GroupName = "general")]
    [Route("api/v1")]
    [ApiController]
    public partial class GeneralController : BaseController
    {
        private readonly IMemoryCache _memoryCache;

        public GeneralController(IConfiguration configuration, IMemoryCache memoryCache)
            : base(configuration, memoryCache)
        {
            _memoryCache = memoryCache;
        }

        // ✅ Consulta general (sin ID)
        /*[Authorize]*/
        [HttpGet("consultar")]
        public async Task<IActionResult> ConsultarGeneral([FromQuery] string? table = "general")
        {
            /*int userId;
            try { userId = ObtenerUsuarioId(); }
            catch (UnauthorizedAccessException ex) { return Unauthorized(new { Message = ex.Message }); }*/

            string cacheKey = $"general_all_{table}";
            if (_memoryCache.TryGetValue(cacheKey, out List<Dictionary<string, object>> cachedResults))
                return Ok(cachedResults);

            string query = $"SELECT * FROM {table}";

            await using var connection = await OpenConnectionAsync();
            await using var command = new SqlCommand(query, connection);

            await using var reader = await command.ExecuteReaderAsync();
            var results = new List<Dictionary<string, object>>();

            while (await reader.ReadAsync())
            {
                var row = new Dictionary<string, object>();
                for (int i = 0; i < reader.FieldCount; i++)
                    row[reader.GetName(i)] = reader.GetValue(i);
                results.Add(row);
            }

            _memoryCache.Set(cacheKey, results, TimeSpan.FromMinutes(5));

            return Ok(results);
        }
        // ✅ Consulta por ID
        /*[Authorize]*/
        [HttpGet("consultar/{id}")]
        public async Task<IActionResult> ConsultarPorId(int id, [FromQuery] string? table = "general")
        {
            /*int userId;
            try { userId = ObtenerUsuarioId(); }
            catch (UnauthorizedAccessException ex) { return Unauthorized(new { Message = ex.Message }); }*/

            string cacheKey = $"general_{table}_{id}";
            if (_memoryCache.TryGetValue(cacheKey, out List<Dictionary<string, object>> cachedResults))
                return Ok(cachedResults);

            string query = $"SELECT * FROM {table} WHERE id = @ID";

            await using var connection = await OpenConnectionAsync();
            await using var command = new SqlCommand(query, connection);
            command.Parameters.AddWithValue("@ID", id);

            await using var reader = await command.ExecuteReaderAsync();
            var results = new List<Dictionary<string, object>>();

            while (await reader.ReadAsync())
            {
                var row = new Dictionary<string, object>();
                for (int i = 0; i < reader.FieldCount; i++)
                    row[reader.GetName(i)] = reader.GetValue(i);
                results.Add(row);
            }

            _memoryCache.Set(cacheKey, results, TimeSpan.FromMinutes(5));
            return Ok(results);
        }
        /*[Authorize]*/
        [HttpPost("consultar/filtros")]
        public async Task<IActionResult> ConsultarGeneralConFiltros(
            [FromBody] FiltrosRequest request,
            [FromQuery] string? table = "general",
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 10)
        {
            if (page <= 0) page = 1;
            if (pageSize <= 0) pageSize = 10;

            int offset = (page - 1) * pageSize;

            // Construir la cláusula SELECT basada en los selects solicitados
            string selectClause = BuildSelectClause(request);
            var baseQuery = $"FROM {table}";
            var whereClauses = new List<string>();
            var parameters = new List<SqlParameter>();
            var parameterCounters = new Dictionary<string, int>();

            // Procesar filtros usando el método BuildFilters (solo procesará los no vacíos)
            BuildFilters(request, whereClauses, parameters, parameterCounters);

            // Agrupar condiciones para el mismo campo con OR
            var groupedWhereClauses = AgruparCondiciones(whereClauses);

            var whereQuery = groupedWhereClauses.Any()
                ? $"WHERE {string.Join(" AND ", groupedWhereClauses)}"
                : "";

            // Construir la cláusula ORDER BY (solo procesará los no vacíos)
            string orderByClause = BuildOrderByClause(request);

            var countQuery = $@"SELECT COUNT(*) AS TotalRegistros {baseQuery} {whereQuery}";

            var paginatedQuery = $@"
        SELECT {selectClause}
        {baseQuery} {whereQuery}
        {orderByClause}
        OFFSET @Offset ROWS FETCH NEXT @PageSize ROWS ONLY";

            try
            {
                await using var connection = await OpenConnectionAsync();

                // Total records
                var countCommandParameters = parameters
                    .Select(p => new SqlParameter(p.ParameterName, p.Value))
                    .ToList();

                await using var countCommand = new SqlCommand(countQuery, connection);
                countCommand.Parameters.AddRange(countCommandParameters.ToArray());
                var totalRecords = (int)await countCommand.ExecuteScalarAsync();

                // Paginated data
                var paginatedParameters = parameters
                    .Select(p => new SqlParameter(p.ParameterName, p.Value))
                    .ToList();

                paginatedParameters.AddRange(new[]
                {
            new SqlParameter("@Offset", offset),
            new SqlParameter("@PageSize", pageSize)
        });

                await using var command = new SqlCommand(paginatedQuery, connection);
                command.Parameters.AddRange(paginatedParameters.ToArray());

                var results = new List<Dictionary<string, object>>();

                await using var reader = await command.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    var row = new Dictionary<string, object>();
                    for (int i = 0; i < reader.FieldCount; i++)
                    {
                        row[reader.GetName(i)] = reader.GetValue(i);
                    }
                    results.Add(row);
                }

                // Crear clave de caché única basada en los filtros válidos
                var validFiltros = request.Filtros
                    .Where(f => !string.IsNullOrWhiteSpace(f.Key) && !string.IsNullOrWhiteSpace(f.Value))
                    .ToList();

                var filtrosCacheKey = string.Join("_", validFiltros
                    .Select(f => $"{f.Key}_{f.Value}_{f.Operator}"));

                var cacheKey = $"general_filtros_{filtrosCacheKey}_page{page}_size{pageSize}";
                _memoryCache.Set(cacheKey, results, TimeSpan.FromMinutes(5));

                return Ok(new
                {
                    TotalRecords = totalRecords,
                    TotalPages = (int)Math.Ceiling(totalRecords / (double)pageSize),
                    PageSize = pageSize,
                    Page = page,
                    Data = results
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { Message = "Error interno del servidor", Details = ex.Message });
            }
        }
        // ✅ Registro dinámico con JSON
        /*[Authorize]*/
        [HttpPost("register")]
        public async Task<IActionResult> Registrar([FromBody] JObject data, [FromQuery] string? table = "general")
        {
            if (data == null) return BadRequest(new { Message = "JSON inválido" });

            /*int userId;
            try { userId = ObtenerUsuarioId(); }
            catch (UnauthorizedAccessException ex) { return Unauthorized(new { Message = ex.Message }); }*/


            // 🔑 Construcción dinámica de columnas y parámetros
            var columnNames = string.Join(", ", data.Properties().Select(p => $"[{p.Name}]"));
            var parameterNames = string.Join(", ", data.Properties().Select(p => $"@{p.Name}"));

            var query = $@"
            INSERT INTO [{table}] ({columnNames})
            OUTPUT INSERTED.id
            VALUES ({parameterNames});";

            await using var connection = await OpenConnectionAsync();
            await using var command = new SqlCommand(query, connection);

            foreach (var prop in data.Properties())
                command.Parameters.AddWithValue("@" + prop.Name, prop.Value?.ToObject<object>() ?? DBNull.Value);

            var insertedId = await command.ExecuteScalarAsync();

            if (insertedId != null)
            {
                _memoryCache.Remove($"general_all_{table}");
            }

            return Ok(new { Message = "Registro exitoso", Id = insertedId });
        }

        // ✅ Actualización dinámica
        /*[Authorize]*/
        [HttpPut("update/{id}")]
        public async Task<IActionResult> Actualizar(int id, [FromBody] JObject data, [FromQuery] string? table = "general")
        {
            if (data == null) return BadRequest(new { Message = "JSON inválido" });

            /*int userId;
            try { userId = ObtenerUsuarioId(); }
            catch (UnauthorizedAccessException ex) { return Unauthorized(new { Message = ex.Message }); }*/

            var setClause = string.Join(",", data.Properties().Select(p => $"{p.Name} = @{p.Name}"));
            string query = $"UPDATE {table} SET {setClause} WHERE id = @Id";

            await using var connection = await OpenConnectionAsync();
            await using var command = new SqlCommand(query, connection);
            command.Parameters.AddWithValue("@Id", id);

            foreach (var prop in data.Properties())
                command.Parameters.AddWithValue("@" + prop.Name, prop.Value?.ToObject<object>() ?? DBNull.Value);

            var result = await command.ExecuteNonQueryAsync();

            if (result > 0)
            {
                _memoryCache.Remove($"general_{table}_{id}");
                _memoryCache.Remove($"general_all_{table}");
                return Ok(new { Message = "Actualización exitosa" });
            }

            return NotFound(new { Message = "Registro no encontrado" });
        }

        // ✅ Eliminación lógica
        /*[Authorize]*/
        [HttpDelete("archivar/{id}")]
        public async Task<IActionResult> Archivar(int id, [FromQuery] string? table = "general")
        {
            /*int userId;
            try { userId = ObtenerUsuarioId(); }
            catch (UnauthorizedAccessException ex) { return Unauthorized(new { Message = ex.Message }); }*/

            string query = $"UPDATE {table} SET estado = 'archivado' WHERE id = @Id";

            await using var connection = await OpenConnectionAsync();
            await using var command = new SqlCommand(query, connection);
            command.Parameters.AddWithValue("@Id", id);

            var result = await command.ExecuteNonQueryAsync();

            if (result > 0)
            {
                _memoryCache.Remove($"general_{table}_{id}");
                _memoryCache.Remove($"general_all_{table}");
                return Ok(new { Message = "Registro eliminado exitosamente" });
            }

            return NotFound(new { Message = "Registro no encontrado" });
        }
        // ✅ Eliminación lógica
        /*[Authorize]*/
        [HttpDelete("delete/{id}")]
        public async Task<IActionResult> Eliminar(int id, [FromQuery] string? table = "general")
        {
            /*int userId;
            try { userId = ObtenerUsuarioId(); }
            catch (UnauthorizedAccessException ex) { return Unauthorized(new { Message = ex.Message }); }*/

            string query = $"DELETE {table} WHERE id = @Id";

            await using var connection = await OpenConnectionAsync();
            await using var command = new SqlCommand(query, connection);
            command.Parameters.AddWithValue("@Id", id);

            var result = await command.ExecuteNonQueryAsync();

            if (result > 0)
            {
                _memoryCache.Remove($"general_{table}_{id}");
                _memoryCache.Remove($"general_all_{table}");
                return Ok(new { Message = "Registro eliminado exitosamente" });
            }

            return NotFound(new { Message = "Registro no encontrado" });
        }
    }
}
