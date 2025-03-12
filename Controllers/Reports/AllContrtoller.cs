using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Caching.Memory;
using System.Data;

namespace MyApiProject.Controllers
{
    public partial class Reporteria : BaseController
    {
        [HttpPost("api/v1/reporteria/all")]
        public async Task<IActionResult> ObtenerAll(
            [FromBody] ReporteriaRequest request,
            [FromQuery] bool sum = false,
            [FromQuery] bool distinct = false,
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 10)
        {
            if (sum && distinct)
            {
                return BadRequest("Los parámetros sum y distinct no pueden ser verdaderos al mismo tiempo.");
            }

            page = Math.Max(page, 1);
            pageSize = Math.Max(pageSize, 5);
            int offset = (page - 1) * pageSize;

            const string baseQuery = "FROM [LOCAL_TC032391E].[dbo].[Temp_MovimientosReport]";

            var whereClauses = new List<string>();
            var sumaClauses = new List<string>();
            var parameters = new List<SqlParameter>();
            var parameterCounters = new Dictionary<string, int>();

            BuildFilters(request, whereClauses, parameters, parameterCounters);
            foreach (var suma in request.Sumas)
            {
                if (!string.IsNullOrWhiteSpace(suma.Key))
                    sumaClauses.Add(suma.Key);
            }

            var condicionesAgrupadas = AgruparCondiciones(whereClauses);
            string whereQuery = condicionesAgrupadas.Any() ? $"WHERE {string.Join(" AND ", condicionesAgrupadas)}" : "";
            string sumaQuery = sumaClauses.Any() ? string.Join(", ", sumaClauses) : "";

            // Construcción de consultas según sum y distinct
            string dataQuery;
            string countQuery;

            if (sum)
            {
                dataQuery = $@"
                    WITH SumData AS (
                        SELECT 
                            {(string.IsNullOrEmpty(sumaQuery) ? "" : $"{sumaQuery},")}
                            SUM(Cantidad) AS Cantidad,
                            SUM(ImporteTotal) AS Importe,
                            SUM(CostoTotal) AS Costo
                        {baseQuery}
                        {whereQuery}
                        {(string.IsNullOrEmpty(sumaQuery) ? "" : $"GROUP BY {sumaQuery}")}
                    )
                    SELECT * FROM SumData
                    ORDER BY Importe DESC
                    OFFSET @Offset ROWS FETCH NEXT @PageSize ROWS ONLY";

                countQuery = $"SELECT COUNT(1) AS TotalRegistros FROM (SELECT DISTINCT Nombre {baseQuery} {whereQuery}) AS Subquery";
            }
            else if (distinct)
            {
                dataQuery = $@"
                    SELECT DISTINCT {(string.IsNullOrEmpty(sumaQuery) ? "*" : sumaQuery)}
                    {baseQuery}
                    {whereQuery}
                    ORDER BY {(string.IsNullOrEmpty(sumaQuery) ? "FechaEmision" : sumaQuery)} DESC
                    OFFSET @Offset ROWS FETCH NEXT @PageSize ROWS ONLY";

                countQuery = string.IsNullOrEmpty(sumaQuery)
                    ? $"SELECT COUNT(*) AS TotalRegistros FROM (SELECT DISTINCT * {baseQuery} {whereQuery}) AS Subquery"
                    : $"SELECT COUNT(*) AS TotalRegistros FROM (SELECT DISTINCT {sumaQuery} {baseQuery} {whereQuery}) AS Subquery";
            }
            else
            {
                dataQuery = $@"
                    SELECT 
                        {(string.IsNullOrEmpty(sumaQuery) ? "*" : sumaQuery)}
                    {baseQuery}
                    {whereQuery}
                    ORDER BY FechaEmision DESC
                    OFFSET @Offset ROWS FETCH NEXT @PageSize ROWS ONLY";

                countQuery = $"SELECT COUNT(*) AS TotalRegistros {baseQuery} {whereQuery}";
            }

            string combinedQuery = $"{countQuery}; {dataQuery}";

            parameters.Add(new SqlParameter("@Offset", offset));
            parameters.Add(new SqlParameter("@PageSize", pageSize));

            try
            {
                var cacheKey = $"{combinedQuery}-{offset}-{pageSize}";
                if (_memoryCache.TryGetValue(cacheKey, out var cachedResult))
                {
                    return Ok(cachedResult);
                }

                await using var connection = await OpenConnectionAsync();
                await using var command = new SqlCommand(combinedQuery, connection)
                {
                    CommandTimeout = 30
                };
                command.Parameters.AddRange(parameters.ToArray());

                await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess);

                int totalRecords = 0;
                if (await reader.ReadAsync())
                {
                    totalRecords = Convert.ToInt32(reader["TotalRegistros"]);
                }

                await reader.NextResultAsync();

                var results = new List<Dictionary<string, object>>();
                while (await reader.ReadAsync())
                {
                    var row = new Dictionary<string, object>(reader.FieldCount);
                    object[] values = new object[reader.FieldCount];
                    reader.GetValues(values);
                    for (int i = 0; i < reader.FieldCount; i++)
                    {
                        row[reader.GetName(i)] = values[i];
                    }
                    results.Add(row);
                }

                var response = new
                {
                    TotalRecords = totalRecords,
                    TotalPages = (int)Math.Ceiling(totalRecords / (double)pageSize),
                    Page = page,
                    PageSize = pageSize,
                    Data = results
                };

                _memoryCache.Set(cacheKey, response, TimeSpan.FromMinutes(10)); // Cache por 10 minutos

                return Ok(response);
            }
            catch (Exception ex)
            {
                return HandleException(ex, combinedQuery);
            }
        }

        private List<string> AgruparCondiciones(List<string> whereClauses)
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

        public void BuildFilters(ReporteriaRequest request, List<string> whereClauses, List<SqlParameter> parameters, Dictionary<string, int> parameterCounters)
        {
            // Procesar filtros
            var fechaEmisionParams = request.Filtros.Where(f => f.Key == "FechaEmision").ToList();
            bool fechaRangeProcessed = false;

            // Manejo de rango de fechas si hay exactamente dos filtros
            if (fechaEmisionParams.Count == 2)
            {
                var minFecha = fechaEmisionParams.FirstOrDefault(f => f.Operator == ">=");
                var maxFecha = fechaEmisionParams.FirstOrDefault(f => f.Operator == "<=");

                if (minFecha != null && maxFecha != null)
                {
                    whereClauses.Add("FechaEmision BETWEEN @FechaEmisionMin AND @FechaEmisionMax");
                    parameters.Add(new SqlParameter("@FechaEmisionMin", DateTime.Parse(minFecha.Value)));
                    parameters.Add(new SqlParameter("@FechaEmisionMax", DateTime.Parse(maxFecha.Value)));
                    fechaRangeProcessed = true;
                }
            }

            // Procesar otros filtros (excluyendo los de fecha si ya se procesaron)
            foreach (var filter in request.Filtros)
            {
                string operatorClause = filter.Operator?.ToLower() switch
                {
                    "like" => "LIKE",
                    "=" => "=",
                    ">=" => ">=",
                    "<=" => "<=",
                    ">" => ">",
                    "<" => "<",
                    "<>" => "<>",
                    _ => "LIKE"
                };
                if (fechaRangeProcessed && filter.Key == "FechaEmision") continue;

                if (!string.IsNullOrWhiteSpace(filter.Value) && filter.Key == "Codigo")
                {
                    // Manejar filtro Codigo con subquery
                    var columnName = filter.Key;

                    // Generar nombre de parámetro único
                    if (!parameterCounters.ContainsKey(columnName))
                        parameterCounters[columnName] = 0;
                    else
                        parameterCounters[columnName]++;

                    var uniqueParameterName = $"@{columnName.Replace(".", "_")}_{parameterCounters[columnName]}";
                    whereClauses.Add($"Articulo IN (SELECT Articulo FROM [LOCAL_TC032391E].[dbo].[Temp_AllReport] WHERE Codigo {operatorClause} {uniqueParameterName})");

                    object paramValue = operatorClause == "LIKE"
                       ? $"%{filter.Value}%"
                       : filter.Value;
                    //Console.Write(paramValue);
                    parameters.Add(new SqlParameter(uniqueParameterName, paramValue));
                }
                else
                if (!string.IsNullOrWhiteSpace(filter.Value))
                {
                    var columnName = filter.Key;

                    // Generar nombres de parámetros únicos para otros campos
                    if (!parameterCounters.ContainsKey(columnName))
                        parameterCounters[columnName] = 0;
                    else
                        parameterCounters[columnName]++;

                    var uniqueParameterName = $"@{columnName.Replace(".", "_")}_{parameterCounters[columnName]}";
                    whereClauses.Add($"{columnName} {operatorClause} {uniqueParameterName}");

                    object paramValue = operatorClause == "LIKE"
                        ? $"%{filter.Value}%"
                        : filter.Value;

                    parameters.Add(new SqlParameter(uniqueParameterName, paramValue));
                }
            }
        }
    }
}