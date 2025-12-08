using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.SqlClient;
using Microsoft.AspNetCore.Authorization;
using Newtonsoft.Json.Linq;
using Microsoft.Extensions.Caching.Memory;
using MyApiProject.Models;
using Microsoft.AspNetCore.SignalR;
using MyApiProject.Hubs;
using MyApiProject.Attributes;

namespace MyApiProject.Controllers.general
{
    [ApiExplorerSettings(GroupName = "general")]
    [Route("api/v1")]
    [ApiController]
    public partial class GeneralController : BaseController
    {
        private readonly IMemoryCache _memoryCache;

        private readonly IHubContext<GeneralHubs> _hubContext;

        public GeneralController(IConfiguration configuration, IMemoryCache memoryCache,
            IHubContext<GeneralHubs> hubContext)
            : base(configuration, memoryCache)
        {
            _memoryCache = memoryCache;
            _hubContext = hubContext;
        }
        [ValidateToken] // ← Protege solo este endpoint
        [HttpOptions("test-cors")]
        public IActionResult TestCors()
        {
            return Ok(new
            {
                Message = "CORS configurado correctamente",
                AllowedMethods = "GET, POST, PUT, DELETE, PATCH, OPTIONS"
            });
        }
        // ✅ Consulta general (sin ID)
        [HttpGet("consultar")]
        [ValidateToken] // ← Protege solo este endpoint
        public async Task<IActionResult> ConsultarGeneral([FromQuery] string? table = "general")
        {
            string cacheKey = $"general_all_{table}";
            if (_memoryCache.TryGetValue(cacheKey, out List<Dictionary<string, object>> cachedResults))
            {
                // Notificar a los clientes que se accedió a los datos
                await _hubContext.Clients.Group("PedidosGeneral")
                    .SendAsync("ConsultaRealizada", new
                    {
                        Tabla = table,
                        Tipo = "ConsultaGeneral",
                        Timestamp = DateTime.UtcNow
                    });

                return Ok(cachedResults);
            }

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

            // Notificar a todos los clientes conectados
            await _hubContext.Clients.Group("PedidosGeneral")
                .SendAsync("DatosActualizados", new
                {
                    Tabla = table,
                    Accion = "Consulta",
                    TotalRegistros = results.Count,
                    Timestamp = DateTime.UtcNow
                });
            return Ok(results);
        }

        // ✅ Consulta por ID
        [HttpGet("consultar/{id}")]
        [ValidateToken] // ← Protege solo este endpoint
        public async Task<IActionResult> ConsultarPorId(int id, [FromQuery] string? table = "general")
        {
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
            // Notificar a todos los clientes conectados
            await _hubContext.Clients.Group("PedidosGeneral")
                .SendAsync("DatosActualizados", new
                {
                    Tabla = table,
                    Accion = "ConsultaID",
                    TotalRegistros = results.Count,
                    Timestamp = DateTime.UtcNow
                });
            return Ok(results);
        }

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

            // Construir la cláusula SELECT y GROUP BY
            var (selectClause, groupByClause) = BuildSelectClause(request);
            var baseQuery = $"FROM {table}";
            var whereClauses = new List<string>();
            var parameters = new List<SqlParameter>();
            var parameterCounters = new Dictionary<string, int>();

            // Procesar filtros
            BuildFilters(request, whereClauses, parameters, parameterCounters);

            // Agrupar condiciones
            var groupedWhereClauses = AgruparCondiciones(whereClauses);

            var whereQuery = groupedWhereClauses.Any()
                ? $"WHERE {string.Join(" AND ", groupedWhereClauses)}"
                : "";

            // Construir ORDER BY
            string orderByClause = BuildOrderByClause(request);

            string countColumns;

            if (!string.IsNullOrEmpty(groupByClause))
            {
                // Usar alias si existe, sino usar Key
                countColumns = string.Join(", ", request.Selects
                    .Where(s => !string.IsNullOrWhiteSpace(s.Key))
                    .Select(s =>
                        !string.IsNullOrWhiteSpace(s.Alias)
                            ? $"{s.Key} AS {s.Alias}"  // 👈 aplica alias en el SELECT del subquery
                            : s.Key
                    ));
            }
            else
            {
                countColumns = GetGroupByColumnsForCount(request);
            }


            var countQuery = $@"
            SELECT COUNT(*) AS TotalRegistros 
            FROM (
                SELECT {countColumns}
                {baseQuery} {whereQuery}
                {groupByClause}
            ) AS CountTable";

            // Construir query principal de manera más segura
            var queryBuilder = new System.Text.StringBuilder();
            queryBuilder.Append($"SELECT {selectClause} ");
            queryBuilder.Append($"{baseQuery} ");

            if (!string.IsNullOrEmpty(whereQuery))
                queryBuilder.Append($"{whereQuery} ");

            if (!string.IsNullOrEmpty(groupByClause))
                queryBuilder.Append($"{groupByClause} ");

            if (string.IsNullOrEmpty(orderByClause))
                queryBuilder.Append("ORDER BY id ");
            else
                queryBuilder.Append($"{orderByClause} ");

            queryBuilder.Append("OFFSET @Offset ROWS FETCH NEXT @PageSize ROWS ONLY");

            var paginatedQuery = queryBuilder.ToString();

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

                // Cache
                var validFiltros = request.Filtros
                    .Where(f => !string.IsNullOrWhiteSpace(f.Key) && !string.IsNullOrWhiteSpace(f.Value))
                    .ToList();

                var filtrosCacheKey = string.Join("_", validFiltros
                    .Select(f => $"{f.Key}_{f.Value}_{f.Operator}"));

                var cacheKey = $"general_filtros_{filtrosCacheKey}_page{page}_size{pageSize}";
                _memoryCache.Set(cacheKey, results, TimeSpan.FromMinutes(5));

                // Notificar a todos los clientes conectados
                await _hubContext.Clients.Group("PedidosGeneral")
                    .SendAsync("DatosActualizados", new
                    {
                        Tabla = table,
                        Accion = "ConsultaFiltros",
                        TotalRegistros = results.Count,
                        Timestamp = DateTime.UtcNow
                    });
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
                return StatusCode(500, new { Message = "Error interno del servidor", Details = ex.Message, counter = countQuery, query = paginatedQuery });
            }
        }
        // ✅ Registro dinámico con SignalR
        [HttpPost("register")]
        [ValidateToken] // ← Protege solo este endpoint
        public async Task<IActionResult> Registrar([FromBody] JObject data, [FromQuery] string? table = "general")
        {
            if (data == null) return BadRequest(new { Message = "JSON inválido" });

            try
            {
                Console.WriteLine($"=== REGISTER DEBUG ===");
                Console.WriteLine($"Tabla: {table}");
                Console.WriteLine($"JSON recibido: {data.ToString()}");

                var columnNames = string.Join(", ", data.Properties().Select(p => $"[{p.Name}]"));
                var parameterNames = string.Join(", ", data.Properties().Select(p => $"@{p.Name}"));

                var query = $@"
                    INSERT INTO {table} ({columnNames})
                    VALUES ({parameterNames});";

                Console.WriteLine($"Query: {query}");

                await using var connection = await OpenConnectionAsync();
                Console.WriteLine("Conexión abierta exitosamente");

                await using var command = new SqlCommand(query, connection);

                foreach (var prop in data.Properties())
                {
                    var value = prop.Value?.ToObject<object>() ?? DBNull.Value;
                    Console.WriteLine($"Parámetro: @{prop.Name} = {value} (Tipo: {value.GetType()})");
                    command.Parameters.AddWithValue("@" + prop.Name, value);
                }

                Console.WriteLine("Ejecutando consulta...");
                await using var reader = await command.ExecuteReaderAsync();
                Console.WriteLine("Consulta ejecutada");

                await _hubContext.Clients.Group("PedidosGeneral")
                    .SendAsync("NuevoRegistro", new
                    {
                        Tabla = table,
                        Accion = "Insert",
                        Timestamp = DateTime.UtcNow
                    });
                _memoryCache.Remove($"general_all_{table}");
                return Ok(new { Message = "Registro exitoso", Data = reader });
            }
            catch (SqlException sqlEx)
            {
                Console.WriteLine($"SQL Error: {sqlEx.Message}");
                Console.WriteLine($"SQL Number: {sqlEx.Number}");
                Console.WriteLine($"Procedure: {sqlEx.Procedure}");
                Console.WriteLine($"Line Number: {sqlEx.LineNumber}");

                return StatusCode(500, new
                {
                    Message = "Error de base de datos",
                    Details = sqlEx.Message,
                    ErrorNumber = sqlEx.Number,
                    LineNumber = sqlEx.LineNumber
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"General Error: {ex.Message}");
                Console.WriteLine($"Stack Trace: {ex.StackTrace}");
                if (ex.InnerException != null)
                {
                    Console.WriteLine($"Inner Exception: {ex.InnerException.Message}");
                }

                return StatusCode(500, new
                {
                    Message = "Error interno del servidor",
                    Details = ex.Message,
                    StackTrace = ex.StackTrace,
                    InnerException = ex.InnerException?.Message
                });
            }
        }

        // ✅ Actualización dinámica - CORREGIDO: Devuelve todos los datos actualizados
        [HttpPut("update/{id}")]
        [ValidateToken] // ← Protege solo este endpoint
        public async Task<IActionResult> Actualizar(int id, [FromBody] JObject data, [FromQuery] string? column = "id", [FromQuery] string? table = "general")
        {
            if (data == null) return BadRequest(new { Message = "JSON inválido" });
            var setClause = string.Join(",", data.Properties().Select(p => $"{p.Name} = @{p.Name}"));

            string query = $@"
            UPDATE {table} 
            SET {setClause} 
            WHERE {column} = @Id";

            await using var connection = await OpenConnectionAsync();
            await using var command = new SqlCommand(query, connection);
            command.Parameters.AddWithValue("@Id", id);

            foreach (var prop in data.Properties())
                command.Parameters.AddWithValue("@" + prop.Name, prop.Value?.ToObject<object>() ?? DBNull.Value);

            await using var reader = await command.ExecuteReaderAsync();

            // Notificar a todos los clientes sobre la actualización
            await _hubContext.Clients.Group("PedidosGeneral")
                .SendAsync("RegistroActualizado", new
                {
                    Tabla = table,
                    RegistroId = id,
                    Accion = "Update",
                    Timestamp = DateTime.UtcNow
                });

            // Notificar específicamente al grupo del pedido si existe
            await _hubContext.Clients.Group($"Pedido_{id}")
                .SendAsync("PedidoActualizado", new
                {
                    Tabla = table,
                    RegistroId = id,
                    Accion = "Update",
                    Timestamp = DateTime.UtcNow
                });

            _memoryCache.Remove($"general_{table}_{id}");
            _memoryCache.Remove($"general_all_{table}");
            return Ok(new { Message = "Actualización exitosa", Data = reader });
        }

        // ✅ Eliminación lógica - CORREGIDO: Devuelve los datos antes de archivar
        [HttpDelete("archivar/{id}")]
        [ValidateToken] // ← Protege solo este endpoint
        public async Task<IActionResult> Archivar(string id, [FromQuery] string? column = "id", [FromQuery] string? table = "general")
        {
            // Primero obtener los datos actuales
            string selectQuery = $"SELECT * FROM {table} WHERE {column} = @Id";
            Dictionary<string, object> originalData = new Dictionary<string, object>();

            await using var connection = await OpenConnectionAsync();

            await using var selectCommand = new SqlCommand(selectQuery, connection);
            selectCommand.Parameters.AddWithValue("@Id", id);

            await using var reader = await selectCommand.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                for (int i = 0; i < reader.FieldCount; i++)
                {
                    originalData[reader.GetName(i)] = reader.GetValue(i);
                }
            }
            await reader.CloseAsync();

            if (originalData.Count == 0)
                return NotFound(new { Message = "Registro no encontrado" });

            // Realizar la actualización
            string updateQuery = $"UPDATE {table} SET estado = 'archivado' WHERE {column} = @Id";
            await using var updateCommand = new SqlCommand(updateQuery, connection);
            updateCommand.Parameters.AddWithValue("@Id", id);

            var result = await updateCommand.ExecuteNonQueryAsync();

            if (result > 0)
            {
                // Notificar a todos los clientes sobre el archivado
                await _hubContext.Clients.Group("PedidosGeneral")
                    .SendAsync("RegistroArchivado", new
                    {
                        Tabla = table,
                        RegistroId = id,
                        DatosOriginales = originalData,
                        Accion = "Archive",
                        Timestamp = DateTime.UtcNow
                    });

                _memoryCache.Remove($"general_{table}_{id}");
                _memoryCache.Remove($"general_all_{table}");
                return Ok(new
                {
                    Message = "Registro archivado exitosamente",
                    Data = originalData
                });
            }

            return NotFound(new { Message = "Registro no encontrado" });
        }

        // ✅ Eliminación física - CORREGIDO: Devuelve los datos antes de eliminar
        [HttpDelete("delete/{id}")]
        [ValidateToken] // ← Protege solo este endpoint
        public async Task<IActionResult> Eliminar(string id, [FromQuery] string? column = "id", [FromQuery] string? table = "general")
        {
            // Primero obtener los datos actuales
            string selectQuery = $"SELECT * FROM {table} WHERE {column} = @Id";
            Dictionary<string, object> originalData = new Dictionary<string, object>();

            await using var connection = await OpenConnectionAsync();

            // Obtener datos originales
            await using var selectCommand = new SqlCommand(selectQuery, connection);
            selectCommand.Parameters.AddWithValue("@Id", id);

            await using var reader = await selectCommand.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                for (int i = 0; i < reader.FieldCount; i++)
                {
                    originalData[reader.GetName(i)] = reader.GetValue(i);
                }
            }
            await reader.CloseAsync();

            if (originalData.Count == 0)
                return NotFound(new { Message = "Registro no encontrado" });

            // Realizar la eliminación
            string deleteQuery = $"DELETE FROM {table} WHERE {column} = @Id";
            await using var deleteCommand = new SqlCommand(deleteQuery, connection);
            deleteCommand.Parameters.AddWithValue("@Id", id);

            var result = await deleteCommand.ExecuteNonQueryAsync();

            if (result > 0)
            {
                // Notificar a todos los clientes sobre el eliminado
                await _hubContext.Clients.Group("PedidosGeneral")
                    .SendAsync("RegistroEliminado", new
                    {
                        Tabla = table,
                        RegistroId = id,
                        DatosOriginales = originalData,
                        Accion = "Delete",
                        Timestamp = DateTime.UtcNow
                    });
                _memoryCache.Remove($"general_{table}_{id}");
                _memoryCache.Remove($"general_all_{table}");
                return Ok(new
                {
                    Message = "Registro eliminado exitosamente",
                    Data = originalData
                });
            }

            return NotFound(new { Message = "Registro no encontrado" });
        }
    }
}