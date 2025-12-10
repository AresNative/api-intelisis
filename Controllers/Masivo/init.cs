using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Caching.Memory;
using MyApiProject.Models;
using System.Data;
using System.Text;

namespace MyApiProject.Controllers
{
    [ApiExplorerSettings(GroupName = "masivo")]
    [Route("api/v2/masivo")]
    [ApiController]
    public class MasivoController : ControllerBase
    {
        private readonly IMemoryCache _memoryCache;
        private readonly IConfiguration _configuration;
        private readonly string _connectionString;

        public MasivoController(IConfiguration configuration, IMemoryCache memoryCache)
        {
            _configuration = configuration;
            _memoryCache = memoryCache;
            _connectionString = configuration.GetConnectionString("DefaultConnection");
        }

        [HttpPost("consultar")]
        public async Task<IActionResult> ConsultarGeneralConFiltros(
            [FromBody] FiltrosRequest request,
            [FromQuery] string? table = "general",
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 50)
        {
            // Validaciones
            if (request == null)
                return BadRequest(new { Message = "Request no puede ser nulo" });

            if (pageSize < 1) pageSize = 50;
            if (pageSize > 1000) pageSize = 1000;
            if (page < 1) page = 1;

            try
            {
                // Cache simple
                var cacheKey = GenerateSimpleCacheKey(request, table, page, pageSize);
                if (_memoryCache.TryGetValue(cacheKey, out object cached))
                {
                    return Ok(cached);
                }

                int offset = (page - 1) * pageSize;

                // Construir SELECT y GROUP BY
                var (selectClause, groupByClause) = BuildCorrectSelectClause(request);

                // Construir WHERE
                var whereClauses = new List<string>();
                var parameters = new List<SqlParameter>();

                BuildSimpleFilters(request, whereClauses, parameters);

                var whereQuery = whereClauses.Any()
                    ? $"WHERE {string.Join(" AND ", whereClauses)}"
                    : "";

                // ORDER BY - Importante: usar alias cuando existan
                string orderByClause = BuildCorrectOrderByClause(request);

                // Query de conteo
                long totalRecords = await GetTotalRecordsAsync(table, whereQuery, groupByClause, parameters, request);

                // Query principal con paginación
                var results = await ExecutePaginatedQueryAsync(
                    selectClause, table, whereQuery, groupByClause,
                    orderByClause, offset, pageSize, parameters);

                // Preparar respuesta
                var response = new
                {
                    PageSize = pageSize,
                    Page = page,
                    TotalRecords = totalRecords,
                    TotalPages = (int)Math.Ceiling(totalRecords / (double)pageSize),
                    Data = results
                };

                // Cachear
                if (results.Count > 0 && results.Count < 1000)
                {
                    _memoryCache.Set(cacheKey, response, TimeSpan.FromMinutes(2));
                }

                return Ok(response);
            }
            catch (Exception ex)
            {
                return StatusCode(500, new
                {
                    Message = "Error interno del servidor",
                    Details = ex.Message,
                    StackTrace = ex.StackTrace
                });
            }
        }

        private string GenerateSimpleCacheKey(FiltrosRequest request, string table, int page, int pageSize)
        {
            var keyParts = new List<string>
            {
                $"masivo_v2_{table}",
                $"page{page}_size{pageSize}"
            };

            if (request.Filtros?.Any() == true)
            {
                var mainFilters = request.Filtros
                    .Where(f => !string.IsNullOrWhiteSpace(f.Key) &&
                           !string.IsNullOrWhiteSpace(f.Value))
                    .Take(3)
                    .Select(f => $"{f.Key}_{f.Value}")
                    .ToList();

                if (mainFilters.Any())
                {
                    keyParts.Add($"filters_{string.Join("_", mainFilters)}");
                }
            }

            return string.Join("|", keyParts);
        }

        private (string selectClause, string groupByClause) BuildCorrectSelectClause(FiltrosRequest request)
        {
            var selectParts = new List<string>();
            var groupByParts = new List<string>();

            // 1. Primero procesar SELECTs normales (no agregaciones)
            if (request.Selects?.Any() == true)
            {
                foreach (var select in request.Selects.Where(s => !string.IsNullOrWhiteSpace(s.Key)))
                {
                    var column = FormatColumnName(select.Key);

                    // SOLO agregar AS si hay alias definido explícitamente
                    if (!string.IsNullOrWhiteSpace(select.Alias))
                    {
                        selectParts.Add($"{column} AS [{select.Alias}]");
                    }
                    else
                    {
                        // Sin alias, solo la columna
                        selectParts.Add(column);
                    }

                    // Para GROUP BY, siempre usar la columna ORIGINAL
                    groupByParts.Add(column);
                }
            }

            // 2. Procesar AGREGACIONES
            if (request.Agregaciones?.Any() == true)
            {
                bool hasAggregations = request.Agregaciones.Any(a =>
                    !string.IsNullOrWhiteSpace(a.Operation) &&
                    a.Operation.ToUpper() != "DISTINCT");

                foreach (var agg in request.Agregaciones.Where(a => !string.IsNullOrWhiteSpace(a.Key)))
                {
                    var operation = GetAggregationOperation(agg.Operation);
                    var columnExpression = FormatColumnExpression(agg.Key);

                    // Para expresiones complejas como "(ventad.Precio * ventad.Cantidad)"
                    // mantenerlas entre paréntesis
                    if (agg.Key.Contains("*") || agg.Key.Contains("/") || agg.Key.Contains("+") || agg.Key.Contains("-"))
                    {
                        if (!agg.Key.StartsWith("(") || !agg.Key.EndsWith(")"))
                        {
                            columnExpression = $"({agg.Key})";
                        }
                    }

                    // SOLO agregar AS si hay alias definido explícitamente
                    if (!string.IsNullOrWhiteSpace(agg.Alias))
                    {
                        if (operation == "DISTINCT")
                        {
                            selectParts.Add($"DISTINCT {columnExpression} AS [{agg.Alias}]");
                        }
                        else
                        {
                            selectParts.Add($"{operation}({columnExpression}) AS [{agg.Alias}]");
                        }
                    }
                    else
                    {
                        // Sin alias
                        if (operation == "DISTINCT")
                        {
                            selectParts.Add($"DISTINCT {columnExpression}");
                        }
                        else
                        {
                            selectParts.Add($"{operation}({columnExpression})");
                        }
                    }

                    // IMPORTANTE: Las columnas usadas en funciones de agregación (SUM, COUNT, AVG, MIN, MAX)
                    // NO deben ir en el GROUP BY a menos que también aparezcan como columnas individuales
                    if (operation == "DISTINCT" || (!hasAggregations && request.Selects?.Any() != true))
                    {
                        // Solo agregar al GROUP BY si es DISTINCT o si no hay otras agregaciones
                        if (!columnExpression.StartsWith("(")) // No agrupar por expresiones complejas
                        {
                            groupByParts.Add(columnExpression);
                        }
                    }
                }
            }

            // 3. Si no hay SELECTs ni AGREGACIONES, usar SELECT *
            if (!selectParts.Any())
            {
                selectParts.Add("*");
            }

            string selectClause = string.Join(", ", selectParts);

            // 4. Determinar GROUP BY clause
            string groupByClause = "";
            if (request.Agregaciones?.Any(a =>
                !string.IsNullOrWhiteSpace(a.Operation) &&
                a.Operation.ToUpper() != "DISTINCT") == true &&
                request.Selects?.Any() == true)
            {
                // Si hay funciones de agregación y columnas normales, necesitamos GROUP BY
                if (groupByParts.Any())
                {
                    groupByClause = $"GROUP BY {string.Join(", ", groupByParts.Distinct())}";
                }
            }
            else if (request.Agregaciones?.Any(a =>
                a.Operation?.ToUpper() == "DISTINCT") == true)
            {
                // Solo DISTINCT, no necesita GROUP BY
                groupByClause = "";
            }

            return (selectClause, groupByClause);
        }

        private string FormatColumnName(string column)
        {
            if (string.IsNullOrWhiteSpace(column))
                return column;

            // Si es una expresión compleja, dejarla tal cual
            if (column.Contains("(") || column.Contains("*") || column.Contains("/") ||
                column.Contains("+") || column.Contains("-"))
            {
                return column;
            }

            // Si tiene punto, formatear como [Tabla].[Columna]
            if (column.Contains("."))
            {
                var parts = column.Split('.');
                if (parts.Length == 2)
                {
                    return $"[{parts[0]}].[{parts[1]}]";
                }
                else if (parts.Length > 2)
                {
                    // Para casos como "schema.table.column"
                    return $"[{string.Join("].[", parts)}]";
                }
            }

            // Columna simple
            return $"[{column}]";
        }

        private string FormatColumnExpression(string expression)
        {
            if (string.IsNullOrWhiteSpace(expression))
                return expression;

            // Si es una expresión matemática, mantenerla entre paréntesis
            if (expression.Contains("*") || expression.Contains("/") ||
                expression.Contains("+") || expression.Contains("-"))
            {
                // Verificar si ya tiene paréntesis
                if (!expression.StartsWith("(") || !expression.EndsWith(")"))
                {
                    return $"({expression})";
                }
                return expression;
            }

            // Formatear nombre de columna normal
            return FormatColumnName(expression);
        }

        private void BuildSimpleFilters(
     FiltrosRequest request,
     List<string> whereClauses,
     List<SqlParameter> parameters)
        {
            if (request.Filtros?.Any() != true) return;

            int paramCounter = parameters.Count; // Usar el conteo actual en lugar de empezar desde 0

            foreach (var filter in request.Filtros.Where(f =>
                !string.IsNullOrWhiteSpace(f.Key) &&
                !string.IsNullOrWhiteSpace(f.Value)))
            {
                string operatorClause = GetOperatorClause(filter.Operator);

                // Para columnas, usar formato correcto
                string column;
                if (filter.Key.Contains("."))
                {
                    var parts = filter.Key.Split('.');
                    column = $"[{parts[0]}].[{parts[1]}]";
                }
                else if (filter.Key.Contains("*") || filter.Key.Contains("/") ||
                         filter.Key.Contains("+") || filter.Key.Contains("-") ||
                         filter.Key.Contains("("))
                {
                    column = filter.Key; // Expresión compleja
                }
                else
                {
                    column = $"[{filter.Key}]";
                }

                // Manejar operadores IN y NOT IN
                if (operatorClause == "IN" || operatorClause == "NOT IN")
                {
                    HandleInOperator(filter, column, operatorClause, whereClauses, parameters, ref paramCounter);
                }
                else if (operatorClause == "LIKE")
                {
                    var paramName = $"@p{paramCounter++}";
                    whereClauses.Add($"{column} LIKE {paramName}");
                    parameters.Add(new SqlParameter(paramName, $"%{filter.Value}%"));
                }
                else
                {
                    var paramName = $"@p{paramCounter++}";
                    whereClauses.Add($"{column} {operatorClause} {paramName}");

                    // Intentar convertir valores numéricos
                    if (decimal.TryParse(filter.Value, out decimal decimalValue))
                    {
                        parameters.Add(new SqlParameter(paramName, decimalValue));
                    }
                    else if (int.TryParse(filter.Value, out int intValue))
                    {
                        parameters.Add(new SqlParameter(paramName, intValue));
                    }
                    else if (DateTime.TryParse(filter.Value, out DateTime dateValue))
                    {
                        parameters.Add(new SqlParameter(paramName, dateValue));
                    }
                    else
                    {
                        parameters.Add(new SqlParameter(paramName, filter.Value));
                    }
                }
            }
        }

        private void HandleInOperator(
            BusquedaParams filter,
            string column,
            string operatorClause,
            List<string> whereClauses,
            List<SqlParameter> parameters,
            ref int paramCounter)
        {
            try
            {
                // Dividir los valores por comas
                var values = filter.Value.Split(',')
                    .Select(v => v.Trim())
                    .Where(v => !string.IsNullOrEmpty(v))
                    .ToArray();

                if (values.Length == 0)
                {
                    // Si no hay valores válidos, no agregar condición
                    return;
                }

                // Caso especial: un solo valor
                if (values.Length == 1)
                {
                    var paramName = $"@p{paramCounter++}";
                    whereClauses.Add($"{column} {operatorClause.Replace("IN", "=").Replace("NOT IN", "<>")} {paramName}");

                    // Determinar tipo del valor
                    if (decimal.TryParse(values[0], out decimal decimalValue))
                    {
                        parameters.Add(new SqlParameter(paramName, decimalValue));
                    }
                    else if (int.TryParse(values[0], out int intValue))
                    {
                        parameters.Add(new SqlParameter(paramName, intValue));
                    }
                    else if (DateTime.TryParse(values[0], out DateTime dateValue))
                    {
                        parameters.Add(new SqlParameter(paramName, dateValue));
                    }
                    else
                    {
                        parameters.Add(new SqlParameter(paramName, values[0]));
                    }
                    return;
                }

                // Crear lista de parámetros para múltiples valores
                var paramNames = new List<string>();
                var paramTypes = new List<SqlDbType>();
                var paramValues = new List<object>();

                // Determinar el tipo de datos basado en el primer valor
                SqlDbType? commonType = null;

                for (int i = 0; i < values.Length; i++)
                {
                    var paramName = $"@p{paramCounter++}";
                    paramNames.Add(paramName);

                    if (int.TryParse(values[i], out int intValue))
                    {
                        paramValues.Add(intValue);
                        paramTypes.Add(SqlDbType.Int);
                        if (commonType == null) commonType = SqlDbType.Int;
                    }
                    else if (decimal.TryParse(values[i], out decimal decimalValue))
                    {
                        paramValues.Add(decimalValue);
                        paramTypes.Add(SqlDbType.Decimal);
                        if (commonType == null) commonType = SqlDbType.Decimal;
                    }
                    else if (DateTime.TryParse(values[i], out DateTime dateValue))
                    {
                        paramValues.Add(dateValue);
                        paramTypes.Add(SqlDbType.DateTime);
                        if (commonType == null) commonType = SqlDbType.DateTime;
                    }
                    else
                    {
                        paramValues.Add(values[i]);
                        paramTypes.Add(SqlDbType.NVarChar);
                        if (commonType == null) commonType = SqlDbType.NVarChar;
                    }
                }

                // Para múltiples valores, asegurarse de que todos sean del mismo tipo
                // Si hay mezcla de tipos, convertir todo a string
                if (paramTypes.Distinct().Count() > 1)
                {
                    paramNames.Clear();
                    paramValues.Clear();
                    paramCounter -= values.Length; // Resetear contador

                    for (int i = 0; i < values.Length; i++)
                    {
                        var paramName = $"@p{paramCounter++}";
                        paramNames.Add(paramName);
                        paramValues.Add(values[i]);
                    }
                }

                // Construir la cláusula IN/NOT IN
                var inClause = $"{column} {operatorClause} ({string.Join(", ", paramNames)})";
                whereClauses.Add(inClause);

                // Agregar parámetros
                for (int i = 0; i < paramNames.Count; i++)
                {
                    parameters.Add(new SqlParameter(paramNames[i], paramValues[i]));
                }
            }
            catch (Exception ex)
            {
                // Log del error (puedes usar ILogger en lugar de Console)
                Console.WriteLine($"Error procesando operador IN/NOT IN: {ex.Message}");

                // Fallback: tratar como igualdad simple
                var paramName = $"@p{paramCounter++}";
                whereClauses.Add($"{column} = {paramName}");
                parameters.Add(new SqlParameter(paramName, filter.Value));
            }
        }

        private string GetOperatorClause(string? operatorStr)
        {
            if (string.IsNullOrWhiteSpace(operatorStr)) return "=";

            return operatorStr.ToUpper() switch
            {
                "LIKE" => "LIKE",
                ">=" => ">=",
                "<=" => "<=",
                ">" => ">",
                "<" => "<",
                "<>" => "<>",
                "!=" => "<>",
                "IN" => "IN",
                "NOT IN" => "NOT IN",
                "BETWEEN" => "BETWEEN",
                "NOT BETWEEN" => "NOT BETWEEN",
                "IS NULL" => "IS NULL",
                "IS NOT NULL" => "IS NOT NULL",
                _ => "="
            };
        }


        private string BuildCorrectOrderByClause(FiltrosRequest request)
        {
            if (request.Order?.Any() != true)
                return "";

            var orderParts = new List<string>();

            foreach (var order in request.Order.Where(o => !string.IsNullOrWhiteSpace(o.Key)))
            {
                var direction = !string.IsNullOrWhiteSpace(order.Direction) &&
                               order.Direction.ToUpper() == "DESC" ? "DESC" : "ASC";

                // Para ORDER BY, usar alias si existe en las agregaciones o selects
                string column;

                // Buscar si esta columna tiene un alias en las agregaciones
                var aggAlias = request.Agregaciones?
                    .FirstOrDefault(a => a.Key == order.Key || a.Alias == order.Key);

                if (aggAlias != null && !string.IsNullOrWhiteSpace(aggAlias.Alias))
                {
                    // Usar el alias de la agregación
                    column = $"[{aggAlias.Alias}]";
                }
                else
                {
                    // Buscar en selects normales
                    var selectAlias = request.Selects?
                        .FirstOrDefault(s => s.Key == order.Key || s.Alias == order.Key);

                    if (selectAlias != null && !string.IsNullOrWhiteSpace(selectAlias.Alias))
                    {
                        column = $"[{selectAlias.Alias}]";
                    }
                    else
                    {
                        // Usar la columna original
                        if (order.Key.Contains("."))
                        {
                            var parts = order.Key.Split('.');
                            column = $"[{parts[0]}].[{parts[1]}]";
                        }
                        else if (order.Key.Contains("*") || order.Key.Contains("/") ||
                                 order.Key.Contains("+") || order.Key.Contains("-") ||
                                 order.Key.Contains("("))
                        {
                            column = order.Key; // Expresión compleja
                        }
                        else
                        {
                            column = $"[{order.Key}]";
                        }
                    }
                }

                orderParts.Add($"{column} {direction}");
            }

            return orderParts.Any() ? $"ORDER BY {string.Join(", ", orderParts)}" : "";
        }

        private string GetAggregationOperation(string? operation)
        {
            if (string.IsNullOrWhiteSpace(operation)) return "COUNT";

            return operation.ToUpper() switch
            {
                "SUM" => "SUM",
                "COUNT" => "COUNT",
                "AVG" => "AVG",
                "MIN" => "MIN",
                "MAX" => "MAX",
                "DISTINCT" => "DISTINCT",
                _ => operation.ToUpper()
            };
        }

        private async Task<long> GetTotalRecordsAsync(
            string table,
            string whereQuery,
            string groupByClause,
            List<SqlParameter> parameters,
            FiltrosRequest request)
        {
            try
            {
                await using var connection = new SqlConnection(_connectionString);
                await connection.OpenAsync();

                string countQuery;

                if (string.IsNullOrEmpty(groupByClause))
                {
                    // Conteo directo para consultas simples
                    countQuery = $"SELECT COUNT_BIG(*) FROM {table} {whereQuery}";
                }
                else
                {
                    // Para consultas con GROUP BY, necesitamos contar los grupos
                    // Construir una versión simple de la consulta para contar
                    var selectColumns = new List<string>();

                    if (request.Selects?.Any() == true)
                    {
                        foreach (var select in request.Selects.Take(1)) // Solo una columna para contar
                        {
                            var column = select.Key.Contains('.') ? select.Key : $"[{select.Key}]";
                            selectColumns.Add(column);
                        }
                    }
                    else if (request.Agregaciones?.Any() == true)
                    {
                        // Si solo hay agregaciones, contar por la primera columna de GROUP BY
                        var groupByColumns = groupByClause.Replace("GROUP BY", "").Trim();
                        if (!string.IsNullOrEmpty(groupByColumns))
                        {
                            var firstColumn = groupByColumns.Split(',').First().Trim();
                            selectColumns.Add(firstColumn);
                        }
                    }

                    if (!selectColumns.Any())
                    {
                        selectColumns.Add("1");
                    }

                    countQuery = $@"
                        SELECT COUNT(*) FROM (
                            SELECT {string.Join(", ", selectColumns)}
                            FROM {table} 
                            {whereQuery} 
                            {groupByClause}
                        ) AS CountTable";
                }

                await using var command = new SqlCommand(countQuery, connection);
                command.CommandTimeout = 30;

                foreach (var param in parameters)
                {
                    if (!command.Parameters.Contains(param.ParameterName))
                    {
                        command.Parameters.AddWithValue(param.ParameterName, param.Value);
                    }
                }

                var result = await command.ExecuteScalarAsync();
                return Convert.ToInt64(result);
            }
            catch (Exception ex)
            {
                // Log del error
                Console.WriteLine($"Error en GetTotalRecordsAsync: {ex.Message}");

                // Para consultas complejas, retornar un valor estimado
                return 10000;
            }
        }
        private string FormatParametersForDebug(List<SqlParameter> parameters)
        {
            var sb = new StringBuilder();
            foreach (var p in parameters)
            {
                sb.AppendLine($"{p.ParameterName} = '{p.Value}'");
            }
            return sb.ToString();
        }

        private async Task<List<Dictionary<string, object>>> ExecutePaginatedQueryAsync(
            string selectClause,
            string table,
            string whereQuery,
            string groupByClause,
            string orderByClause,
            int offset,
            int pageSize,
            List<SqlParameter> parameters)
        {
            var results = new List<Dictionary<string, object>>();

            await using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync();

            // Construir query principal
            var queryBuilder = new StringBuilder();

            // Si hay GROUP BY, necesitamos una estructura especial para paginación
            if (!string.IsNullOrEmpty(groupByClause))
            {
                queryBuilder.AppendLine("WITH PaginatedData AS (");
                queryBuilder.AppendLine($"    SELECT {selectClause},");
                queryBuilder.AppendLine($"    ROW_NUMBER() OVER ({orderByClause}) AS RowNum");
                queryBuilder.AppendLine($"    FROM {table}");

                if (!string.IsNullOrEmpty(whereQuery))
                    queryBuilder.AppendLine($"    {whereQuery}");

                queryBuilder.AppendLine($"    {groupByClause}");
                queryBuilder.AppendLine(")");
                queryBuilder.AppendLine("SELECT * FROM PaginatedData");
                queryBuilder.AppendLine($"WHERE RowNum > {offset} AND RowNum <= {offset + pageSize}");

                if (!string.IsNullOrEmpty(orderByClause))
                {
                    // Remover ORDER BY del CTE y ponerlo al final
                    queryBuilder.AppendLine(orderByClause.Replace("OVER (", "OVER (").Replace("ORDER BY", ""));
                }
            }
            else
            {
                // Consulta normal sin GROUP BY
                queryBuilder.Append($"SELECT {selectClause} ");
                queryBuilder.Append($"FROM {table} ");

                if (!string.IsNullOrEmpty(whereQuery))
                    queryBuilder.Append($"{whereQuery} ");

                if (!string.IsNullOrEmpty(orderByClause))
                    queryBuilder.Append($"{orderByClause} ");
                else
                    queryBuilder.Append("ORDER BY 1 ");

                queryBuilder.Append($"OFFSET {offset} ROWS FETCH NEXT {pageSize} ROWS ONLY");
            }
            // 👇 AQUI IMPRIME EL QUERY COMPLETO EN CONSOLA
            Console.WriteLine("========== QUERY PAGINADO ==========");
            Console.WriteLine(queryBuilder.ToString());
            Console.WriteLine("========== PARÁMETROS ==============");
            Console.WriteLine(FormatParametersForDebug(parameters));
            Console.WriteLine("====================================");
            await using var command = new SqlCommand(queryBuilder.ToString(), connection);
            command.CommandTimeout = 60; // Timeout más largo para consultas complejas

            foreach (var param in parameters)
            {
                if (!command.Parameters.Contains(param.ParameterName))
                {
                    command.Parameters.AddWithValue(param.ParameterName, param.Value);
                }
            }

            await using var reader = await command.ExecuteReaderAsync();

            while (await reader.ReadAsync())
            {
                var row = new Dictionary<string, object>();
                for (int i = 0; i < reader.FieldCount; i++)
                {
                    var value = reader.GetValue(i);
                    row[reader.GetName(i)] = value == DBNull.Value ? null : value;
                }
                results.Add(row);
            }

            return results;
        }

    }
}