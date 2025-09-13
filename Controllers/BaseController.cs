// Controllers/BaseController.cs
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Caching.Memory;
using MyApiProject.Models;
using Newtonsoft.Json;

namespace MyApiProject.Controllers
{
    public abstract class BaseController : ControllerBase
    {
        private readonly string _connectionString;
        protected readonly IMemoryCache _cache;

        public BaseController(IConfiguration configuration, IMemoryCache cache)
        {
            _connectionString = configuration.GetConnectionString("DefaultConnection")
                                ?? throw new InvalidOperationException("Cadena de conexión 'DefaultConnection' no encontrada");
            _cache = cache;
        }

        protected async Task<SqlConnection> OpenConnectionAsync()
        {
            var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync();
            return connection;
        }

        protected IActionResult HandleException(Exception ex, string? query = null)
        {
            if (ex is OperationCanceledException || ex is TaskCanceledException)
                return StatusCode(499, new { Message = "Solicitud cancelada por el cliente" });

            var sanitizedMessage = ex.Message.Replace("\r", "").Replace("\n", " ");
            var sanitizedQuery = query?.Replace("\r", "").Replace("\n", " ");

            return StatusCode(500, new
            {
                Message = $"Error: {sanitizedMessage}",
                Query = sanitizedQuery
            });
        }

        protected IActionResult HandleException(Exception ex, int statusCode)
        {
            if (ex is OperationCanceledException || ex is TaskCanceledException)
                return StatusCode(499, "Solicitud cancelada por el cliente");

            return StatusCode(statusCode, new { Message = $"Error: {ex.Message}" });
        }
        /* protected int GetUserIdFromToken()
        {
            var userIdClaim = User?.Claims?.FirstOrDefault(c => c.Type == "userId");

            if (userIdClaim != null && int.TryParse(userIdClaim.Value, out int userId))
            {
                return userId;
            }

            return 0; // Valor por defecto si no se encuentra el claim
        } */
        /* protected int ObtenerUsuarioId()
        {
            int userId = GetUserIdFromToken();
            if (userId == 0)
                throw new UnauthorizedAccessException("Token no válido o no se pudo extraer el ID del usuario.");
            return userId;
        } */

        protected static int? ExtraerIdDeResultado(IActionResult result)
        {
            if (result is OkObjectResult ok && ok.Value is not null)
            {
                var resultData = JsonConvert.DeserializeObject<dynamic>(
                    JsonConvert.SerializeObject(ok.Value)
                );
                return (int?)resultData?.Id;
            }
            return null;
        }
        protected async Task<IActionResult> InsertJsonToDatabaseAsync<T>(
            T data,
            string tableName,
            IFormFile? file = null,
            string fileColumn = "file",
            Dictionary<string, object>? extraColumns = null,
            Func<SqlConnection, Task<IActionResult>>? preValidation = null
        )
        {
            try
            {
                // Guardar archivo si existe
                string? filePath = null;
                if (file != null)
                {
                    var uploadsFolder = Path.Combine(Directory.GetCurrentDirectory(), "Uploads");
                    Directory.CreateDirectory(uploadsFolder);

                    var fileExtension = Path.GetExtension(file.FileName);
                    var uniqueFileName = $"{Guid.NewGuid()}{fileExtension}";
                    filePath = Path.Combine(uploadsFolder, uniqueFileName);

                    await using var stream = new FileStream(filePath, FileMode.Create);
                    await file.CopyToAsync(stream);
                }

                await using var connection = await OpenConnectionAsync();

                if (preValidation != null)
                {
                    var validationResult = await preValidation(connection);
                    if (validationResult != null) return validationResult;
                }

                // Obtener propiedades del modelo
                var properties = typeof(T).GetProperties()
                    .Where(p => p.GetValue(data) != null)
                    .ToList();

                var allColumns = new List<string>();
                var allParameters = new List<string>();
                var sqlParameters = new List<SqlParameter>();

                foreach (var prop in properties)
                {
                    allColumns.Add($"[{prop.Name}]");
                    allParameters.Add($"@{prop.Name}");
                    sqlParameters.Add(new SqlParameter($"@{prop.Name}", prop.GetValue(data) ?? DBNull.Value));
                }

                // Extra columns (manuales)
                if (extraColumns != null)
                {
                    foreach (var entry in extraColumns)
                    {
                        allColumns.Add($"[{entry.Key}]");
                        allParameters.Add($"@{entry.Key}");
                        sqlParameters.Add(new SqlParameter($"@{entry.Key}", entry.Value ?? DBNull.Value));
                    }
                }

                // Archivo (si se incluye)
                if (filePath != null)
                {
                    allColumns.Add($"[{fileColumn}]");
                    allParameters.Add("@FilePath");
                    sqlParameters.Add(new SqlParameter("@FilePath", filePath));
                }

                var query = $@"
            INSERT INTO [{tableName}] ({string.Join(", ", allColumns)})
            OUTPUT INSERTED.ID
            VALUES ({string.Join(", ", allParameters)});
        ";

                await using var command = new SqlCommand(query, connection);
                command.Parameters.AddRange(sqlParameters.ToArray());

                var insertedId = await command.ExecuteScalarAsync();

                return Ok(new { Message = $"{tableName} insertado correctamente.", Id = insertedId });
            }
            catch (Exception ex)
            {
                return HandleException(ex, $"Error al insertar en {tableName}.");
            }
        }
        protected async Task<IActionResult> UpdateJsonInDatabaseAsync<T>(
            T data,
            string tableName,
            string keyColumn,
            object keyValue,
            Func<SqlConnection, Task<IActionResult?>>? preValidation = null
        )
        {
            try
            {
                await using var connection = await OpenConnectionAsync();

                if (preValidation != null)
                {
                    var validationResult = await preValidation(connection);
                    if (validationResult != null) return validationResult;
                }

                // Obtener propiedades con valores no nulos, excluyendo la clave primaria
                var properties = typeof(T).GetProperties()
                    .Where(p => p.Name.ToLower() != keyColumn.ToLower())
                    .Where(p => p.GetValue(data) != null)
                    .ToList();

                if (!properties.Any())
                {
                    return BadRequest(new { Message = "No se proporcionaron campos para actualizar." });
                }

                var setClause = string.Join(", ", properties.Select(p => $"{p.Name} = @{p.Name}"));
                var query = $"UPDATE {tableName} SET {setClause} WHERE {keyColumn} = @KeyValue";

                await using var command = new SqlCommand(query, connection);

                foreach (var prop in properties)
                {
                    var value = prop.GetValue(data);
                    command.Parameters.AddWithValue($"@{prop.Name}", value);
                }

                command.Parameters.AddWithValue("@KeyValue", keyValue);

                var result = await command.ExecuteNonQueryAsync();

                return result > 0
                    ? Ok(new { Message = "Información actualizada correctamente." })
                    : NotFound(new { Message = "Información no encontrada." });
            }
            catch (Exception ex)
            {
                return HandleException(ex);
            }
        }
        // Métodos agregados
        protected void BuildFilters(FiltrosRequest request, List<string> whereClauses, List<SqlParameter> parameters,
                    Dictionary<string, int> parameterCounters)
        {
            // Filtrar elementos vacíos primero
            var validFiltros = request.Filtros
                .Where(f => !string.IsNullOrWhiteSpace(f.Key) && !string.IsNullOrWhiteSpace(f.Value))
                .ToList();

            var fechaParams = validFiltros.Where(f => f.Key == "Fecha").ToList();
            bool fechaRangeProcessed = false;

            if (fechaParams.Count == 2)
            {
                var minFecha = fechaParams.FirstOrDefault(f => f.Operator == ">=");
                var maxFecha = fechaParams.FirstOrDefault(f => f.Operator == "<=");

                if (minFecha != null && maxFecha != null &&
                    !string.IsNullOrWhiteSpace(minFecha.Value) &&
                    !string.IsNullOrWhiteSpace(maxFecha.Value))
                {
                    whereClauses.Add($"{fechaParams.First().Key} BETWEEN @FechaMin AND @FechaMax");
                    parameters.Add(new SqlParameter("@FechaMin", DateTime.Parse(minFecha.Value)));
                    parameters.Add(new SqlParameter("@FechaMax", DateTime.Parse(maxFecha.Value)));
                    fechaRangeProcessed = true;
                }
            }

            foreach (var filter in validFiltros)
            {
                if (fechaRangeProcessed && filter.Key == "Fecha") continue;

                string operatorClause = filter.Operator?.ToLower() switch
                {
                    "like" => "LIKE",
                    "=" => "=",
                    ">=" => ">=",
                    "<=" => "<=",
                    ">" => ">",
                    "<" => "<",
                    "<>" => "<>",
                    _ => "=" // Valor por defecto más seguro
                };

                var column = filter.Key;
                if (!parameterCounters.ContainsKey(column))
                    parameterCounters[column] = 0;
                else
                    parameterCounters[column]++;

                var paramName = $"@{column}_{parameterCounters[column]}";
                whereClauses.Add($"{column} {operatorClause} {paramName}");

                var paramValue = operatorClause == "LIKE" ? $"%{filter.Value}%" : filter.Value;
                parameters.Add(new SqlParameter(paramName, paramValue));
            }
        }

        protected List<string> AgruparCondiciones(List<string> whereClauses)
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

        protected async Task<string> GuardarArchivo(IFormFile archivo)
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

        // Métodos auxiliares para construir las cláusulas
        protected string GetGroupByColumns(FiltrosRequest request)
        {
            var groupByColumns = request.Selects?
                .Where(s => !string.IsNullOrWhiteSpace(s.Key))
                .Select(s => s.Key)
                .ToList();

            return groupByColumns != null && groupByColumns.Any()
                ? string.Join(", ", groupByColumns)
                : "id";
        }

        // Modifica el método BuildSelectClause
        protected (string selectClause, string groupByClause) BuildSelectClause(FiltrosRequest request)
        {
            var selectParts = new List<string>();
            var groupByParts = new List<string>();

            // Procesar columnas normales (SELECT)
            var validSelects = request.Selects?
                .Where(s => !string.IsNullOrWhiteSpace(s.Key))
                .ToList();

            if (validSelects != null && validSelects.Any())
            {
                foreach (var select in validSelects)
                {
                    selectParts.Add(select.Key);
                    groupByParts.Add(select.Key);
                }
            }

            // Procesar operaciones de agregación
            var validAgregaciones = request.Agregaciones?
                .Where(a => !string.IsNullOrWhiteSpace(a.Key))
                .ToList();

            if (validAgregaciones != null && validAgregaciones.Any())
            {
                foreach (var agregacion in validAgregaciones)
                {
                    string operation = !string.IsNullOrWhiteSpace(agregacion.Operation)
                        ? agregacion.Operation.ToUpper()
                        : "SUM";

                    // Validar operaciones SQL permitidas
                    string sqlOperation = operation switch
                    {
                        "SUM" => "SUM",
                        "COUNT" => "COUNT",
                        "AVG" => "AVG",
                        "MIN" => "MIN",
                        "MAX" => "MAX",
                        "DISTINCT" => "DISTINCT",
                        _ => "SUM" // Valor por defecto
                    };

                    string alias = !string.IsNullOrWhiteSpace(agregacion.Alias)
                        ? agregacion.Alias
                        : $"{sqlOperation}_{agregacion.Key}";

                    if (sqlOperation == "DISTINCT")
                    {
                        selectParts.Add($"DISTINCT {agregacion.Key} AS {alias}");
                    }
                    else
                    {
                        selectParts.Add($"{sqlOperation}({agregacion.Key}) AS {alias}");
                    }
                }
            }

            // Si no hay selects ni agregaciones, devolver todas las columnas
            string selectClause = selectParts.Any() ? string.Join(", ", selectParts) : "*";

            // Solo agregar GROUP BY si hay columnas normales
            string groupByClause = groupByParts.Any()
                ? $"GROUP BY {string.Join(", ", groupByParts)}"
                : "";

            return (selectClause, groupByClause);
        }
        protected string BuildOrderByClause(FiltrosRequest request)
        {
            // Solo procesar órdenes que tengan Key no vacío
            var validOrders = request.Order?
                .Where(o => !string.IsNullOrWhiteSpace(o.Key))
                .ToList();

            if (validOrders == null || !validOrders.Any())
            {
                return "ORDER BY id"; // Orden por defecto
            }

            var orderParts = new List<string>();

            foreach (var order in validOrders)
            {
                var direction = !string.IsNullOrWhiteSpace(order.Direction) &&
                               order.Direction.ToUpper() == "DESC" ? "DESC" : "ASC";
                orderParts.Add($"{order.Key} {direction}");
            }

            return $"ORDER BY {string.Join(", ", orderParts)}";
        }
        protected string GetGroupByColumnsForCount(FiltrosRequest request)
        {
            var selectColumns = request.Selects?
                .Where(s => !string.IsNullOrWhiteSpace(s.Key))
                .Select(s => s.Key)
                .ToList();

            if (selectColumns != null && selectColumns.Any())
            {
                // Si hay columnas para GROUP BY, usar DISTINCT
                return $"DISTINCT {string.Join(", ", selectColumns)}";
            }
            else
            {
                // Si no hay columnas específicas, usar un campo único
                return "id";
            }
        }
    }
}
