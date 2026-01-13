// Controllers/BaseController.cs
using System.Text;
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
                    var uploadsFolder = Path.Combine(Directory.GetCurrentDirectory(), "uploads");
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

                var paramName = $"@{column.Replace(".", "_")}_{parameterCounters[column]}"; // Reemplazar . por _ en parámetros
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
                    if (!string.IsNullOrWhiteSpace(select.Alias))
                    {
                        selectParts.Add($"{select.Key} AS {select.Alias}");
                    }
                    else
                    {
                        selectParts.Add(select.Key);
                    }
                    groupByParts.Add(select.Key);   // 👈 columna original si no hay alias
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
                // Buscar un campo seguro para ordenar por defecto
                var safeOrderField = FindSafeOrderField(request);
                return $"ORDER BY {safeOrderField}";
            }

            var orderParts = new List<string>();

            foreach (var order in validOrders)
            {
                var direction = !string.IsNullOrWhiteSpace(order.Direction) &&
                               order.Direction.ToUpper() == "DESC" ? "DESC" : "ASC";

                // Usar la clave directamente (ya debe estar calificada con tabla si es necesario)
                orderParts.Add($"{order.Key} {direction}");
            }

            return $"ORDER BY {string.Join(", ", orderParts)}";
        }

        // Método auxiliar para encontrar un campo seguro para ordenar
        private string FindSafeOrderField(FiltrosRequest request)
        {
            // Buscar un campo ID en los selects
            var idField = request.Selects?
                .FirstOrDefault(s => !string.IsNullOrWhiteSpace(s.Key) &&
                                   (s.Key.EndsWith(".id") || s.Key.ToLower() == "id"));

            if (idField != null)
            {
                return !string.IsNullOrWhiteSpace(idField.Alias) ? idField.Alias : idField.Key;
            }

            // Buscar cualquier campo en los selects
            var anyField = request.Selects?
                .FirstOrDefault(s => !string.IsNullOrWhiteSpace(s.Key));

            if (anyField != null)
            {
                return !string.IsNullOrWhiteSpace(anyField.Alias) ? anyField.Alias : anyField.Key;
            }

            // Valor por defecto
            return "id";
        }
        protected string GetGroupByColumnsForCount(FiltrosRequest request)
        {
            var selectColumns = request.Selects?
                .Where(s => !string.IsNullOrWhiteSpace(s.Key))
                .Select(s =>
                    !string.IsNullOrWhiteSpace(s.Alias)
                        ? $"{s.Key} AS {s.Alias}"
                        : s.Key
                )
                .ToList();

            if (selectColumns != null && selectColumns.Any())
            {
                return $"DISTINCT {string.Join(", ", selectColumns)}";
            }
            else
            {
                // Buscar un campo ID seguro para el conteo
                var safeIdField = request.Selects?
                    .FirstOrDefault(s => !string.IsNullOrWhiteSpace(s.Key) &&
                                       (s.Key.EndsWith(".id") || s.Key == "id"))?.Key ?? "id";

                return safeIdField;
            }
        }

        // En BaseController.cs, agrega estos métodos mínimos
        protected async Task<SqlConnection> OpenConnectionAsync(int timeoutSeconds = 30)
        {
            var csb = new SqlConnectionStringBuilder(_connectionString)
            {
                CommandTimeout = timeoutSeconds
            };

            var connection = new SqlConnection(csb.ConnectionString);
            await connection.OpenAsync();
            return connection;
        }

        // Método simplificado para construir SELECT
        protected (string selectClause, string groupByClause) BuildDynamicSelectClause(FiltrosRequest request)
        {
            var selectParts = new List<string>();
            var groupByParts = new List<string>();

            // Columnas simples
            foreach (var select in request.Selects.Where(s => !string.IsNullOrEmpty(s.Key)))
            {
                var column = select.Key.Contains('.') ? select.Key : $"t0.{select.Key}";
                var alias = !string.IsNullOrEmpty(select.Alias) ? $" AS {select.Alias}" : "";
                selectParts.Add($"{column}{alias}");
                groupByParts.Add(column);
            }

            // Agregaciones
            foreach (var agg in request.Agregaciones.Where(a => !string.IsNullOrEmpty(a.Key)))
            {
                var column = agg.Key.Contains('.') ? agg.Key : $"t0.{agg.Key}";
                var operation = GetSafeAggregation(agg.Operation);
                var alias = !string.IsNullOrEmpty(agg.Alias) ? $" AS {agg.Alias}" : "";

                if (operation == "DISTINCT")
                    selectParts.Add($"DISTINCT {column}{alias}");
                else
                    selectParts.Add($"{operation}({column}){alias}");
            }

            string selectClause = selectParts.Any() ? string.Join(", ", selectParts) : "t0.*";
            string groupByClause = groupByParts.Any() ? $"GROUP BY {string.Join(", ", groupByParts)}" : "";

            return (selectClause, groupByClause);
        }

        private string GetSafeAggregation(string operation)
        {
            var validOps = new[] { "SUM", "COUNT", "AVG", "MIN", "MAX", "DISTINCT" };
            return validOps.Contains(operation?.ToUpper()) ? operation.ToUpper() : "SUM";
        }

        protected async Task<(List<Dictionary<string, object>> Data, long TotalRecords)>
    ExecutePaginatedQueryAsync(
        FiltrosRequest request,
        string table,
        string whereQuery,
        List<SqlParameter> parameters,
        string selectClause,
        string groupByClause,
        string orderByClause,
        int page,
        int pageSize)
        {
            int offset = (page - 1) * pageSize;

            // Calcular total de registros de manera más eficiente
            long totalRecords = await GetTotalRecordsAsync(table, whereQuery, parameters, groupByClause);

            // Construir query con ROW_NUMBER para mejor rendimiento
            var paginatedQuery = BuildPaginatedQueryWithRowNumber(
                selectClause,
                table,
                whereQuery,
                groupByClause,
                orderByClause,
                offset,
                pageSize);

            await using var connection = await OpenConnectionAsync(180); // Timeout de 3 minutos

            var paginatedParameters = parameters
                .Select(p => new SqlParameter(p.ParameterName, p.Value))
                .ToList();

            paginatedParameters.Add(new SqlParameter("@Offset", offset));
            paginatedParameters.Add(new SqlParameter("@PageSize", pageSize));

            await using var command = new SqlCommand(paginatedQuery, connection);
            command.CommandTimeout = 180; // 3 minutos
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

            return (results, totalRecords);
        }

        private async Task<long> GetTotalRecordsAsync(
            string table,
            string whereQuery,
            List<SqlParameter> parameters,
            string groupByClause)
        {
            try
            {
                await using var connection = await OpenConnectionAsync(60);

                string countQuery;

                if (string.IsNullOrEmpty(groupByClause))
                {
                    // Conteo simple para tablas individuales
                    if (!table.ToUpper().Contains("JOIN"))
                    {
                        countQuery = $@"
                    SELECT COUNT_BIG(*) as Total 
                    FROM {table}
                    {whereQuery}";
                    }
                    else
                    {
                        // Para consultas con JOIN, usamos una subquery
                        countQuery = $@"
                    SELECT COUNT_BIG(*) as Total 
                    FROM (
                        SELECT DISTINCT {GetPrimaryKeyForTable(table)}
                        FROM {table}
                        {whereQuery}
                    ) as SubQuery";
                    }
                }
                else
                {
                    // Para GROUP BY, contamos las filas agrupadas
                    countQuery = $@"
                SELECT COUNT_BIG(*) as Total 
                FROM (
                    SELECT 1
                    FROM {table}
                    {whereQuery}
                    {groupByClause}
                ) as GroupedQuery";
                }

                await using var command = new SqlCommand(countQuery, connection);
                command.Parameters.AddRange(parameters.ToArray());

                var result = await command.ExecuteScalarAsync();
                return Convert.ToInt64(result);
            }
            catch (Exception)
            {
                // Si falla el conteo exacto, devolver estimación
                return await GetEstimatedRowCountAsync(table);
            }
        }

        private async Task<long> GetEstimatedRowCountAsync(string table)
        {
            try
            {
                // Extraer la tabla principal de la consulta
                string mainTable = ExtractMainTable(table);

                await using var connection = await OpenConnectionAsync(30);

                string estimateQuery = @"
            SELECT SUM(row_count) as estimated_total
            FROM sys.dm_db_partition_stats 
            WHERE object_id = OBJECT_ID(@TableName) 
            AND index_id IN (0, 1)";

                await using var command = new SqlCommand(estimateQuery, connection);
                command.Parameters.AddWithValue("@TableName", mainTable);

                var result = await command.ExecuteScalarAsync();
                return result != DBNull.Value ? Convert.ToInt64(result) : 0;
            }
            catch
            {
                return 0;
            }
        }

        private string ExtractMainTable(string fromClause)
        {
            // Extraer la tabla principal del FROM clause
            // Ejemplo: "VENTAD AS INVD INNER JOIN VENTA AS INV ..." → "VENTAD"
            var firstSpaceIndex = fromClause.IndexOf(' ');
            if (firstSpaceIndex > 0)
            {
                return fromClause.Substring(0, firstSpaceIndex).Trim();
            }
            return fromClause.Trim();
        }

        private string GetPrimaryKeyForTable(string table)
        {
            // Determinar la clave primaria basada en la tabla
            var tableLower = table.ToLower();

            if (tableLower.Contains("ventad") || tableLower.Contains("invd"))
                return "INVD.ID";
            else if (tableLower.Contains("venta") || tableLower.Contains("inv"))
                return "INV.ID";
            else if (tableLower.Contains("art"))
                return "ART.Articulo";
            else if (tableLower.Contains("cte"))
                return "C.Cliente";

            return "id"; // Default
        }

        private string BuildPaginatedQueryWithRowNumber(
            string selectClause,
            string table,
            string whereQuery,
            string groupByClause,
            string orderByClause,
            int offset,
            int pageSize)
        {
            // Asegurar un ORDER BY seguro
            string safeOrderBy = GetSafeOrderByForRowNumber(orderByClause, selectClause);

            var query = new StringBuilder();

            if (string.IsNullOrEmpty(groupByClause))
            {
                query.AppendLine($@"
            WITH PaginatedData AS (
                SELECT 
                    {selectClause},
                    ROW_NUMBER() OVER (ORDER BY {safeOrderBy}) AS RowNum
                FROM {table}
                {whereQuery}
            )
            SELECT * FROM PaginatedData
            WHERE RowNum > @Offset AND RowNum <= @Offset + @PageSize
            ORDER BY RowNum");
            }
            else
            {
                query.AppendLine($@"
            WITH GroupedData AS (
                SELECT 
                    {selectClause}
                FROM {table}
                {whereQuery}
                {groupByClause}
            ),
            PaginatedData AS (
                SELECT 
                    *,
                    ROW_NUMBER() OVER (ORDER BY {safeOrderBy}) AS RowNum
                FROM GroupedData
            )
            SELECT * FROM PaginatedData
            WHERE RowNum > @Offset AND RowNum <= @Offset + @PageSize
            ORDER BY RowNum");
            }

            return query.ToString();
        }

        private string GetSafeOrderByForRowNumber(string orderByClause, string selectClause)
        {
            if (!string.IsNullOrEmpty(orderByClause) && orderByClause.StartsWith("ORDER BY "))
            {
                return orderByClause.Substring(9); // Remover "ORDER BY "
            }

            // Buscar una columna segura para ordenar
            var safeColumns = new[] { "id", "ID", "fecha", "Fecha", "fechaemision", "FechaEmision" };

            foreach (var col in safeColumns)
            {
                if (selectClause.Contains(col))
                    return col;
            }

            // Extraer la primera columna del SELECT
            var firstColumn = selectClause.Split(',')[0].Trim();
            var aliasIndex = firstColumn.IndexOf(" AS ", StringComparison.OrdinalIgnoreCase);

            if (aliasIndex > 0)
            {
                return firstColumn.Substring(aliasIndex + 4).Trim(); // Usar el alias
            }

            return firstColumn;
        }

        protected async Task<bool> TablaExiste(string nombreTabla)
        {
            string query = @"
        SELECT COUNT(*) 
        FROM INFORMATION_SCHEMA.TABLES 
        WHERE TABLE_NAME = @NombreTabla";

            await using var connection = await OpenConnectionAsync();
            await using var command = new SqlCommand(query, connection);
            command.Parameters.AddWithValue("@NombreTabla", nombreTabla);

            var resultado = await command.ExecuteScalarAsync();
            return Convert.ToInt32(resultado) > 0;
        }

        protected async Task<List<string>> ObtenerColumnasTabla(string nombreTabla)
        {
            var columnas = new List<string>();

            string query = @"
        SELECT COLUMN_NAME 
        FROM INFORMATION_SCHEMA.COLUMNS 
        WHERE TABLE_NAME = @NombreTabla 
        ORDER BY ORDINAL_POSITION";

            await using var connection = await OpenConnectionAsync();
            await using var command = new SqlCommand(query, connection);
            command.Parameters.AddWithValue("@NombreTabla", nombreTabla);

            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                columnas.Add(reader.GetString(0).ToLower());
            }

            return columnas;
        }
    }
}
