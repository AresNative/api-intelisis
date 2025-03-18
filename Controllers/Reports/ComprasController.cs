using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Caching.Memory;
using System.Data;

namespace MyApiProject.Controllers
{
    public partial class Reporteria : BaseController
    {
        private readonly IMemoryCache _memoryCache;

        public Reporteria(IConfiguration configuration, IMemoryCache memoryCache) : base(configuration)
        {
            _memoryCache = memoryCache;
        }

        [HttpPost("api/v1/reporteria/compras")]
        public async Task<IActionResult> ObtenerCompras(
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
            pageSize = Math.Max(pageSize, 10);
            int offset = (page - 1) * pageSize;

            const string baseQuery = "FROM [LOCAL_TC032391E].[dbo].[Temp_ComprasReport]";

            var whereClauses = new List<string>();
            var sumaClauses = new List<string>();
            var parameters = new List<SqlParameter>();
            var parameterCounters = new Dictionary<string, int>();

            BuildFilters(request, whereClauses, parameters, parameterCounters, "Temp_ComprasReport");
            foreach (var suma in request.Selects)
            {
                if (!string.IsNullOrWhiteSpace(suma.Key))
                    sumaClauses.Add(suma.Key);
            }

            var condicionesAgrupadas = AgruparCondiciones(whereClauses);
            string whereQuery = condicionesAgrupadas.Any() ? $"WHERE {string.Join(" AND ", condicionesAgrupadas)}" : "";
            string sumaQuery = sumaClauses.Any() ? string.Join(", ", sumaClauses) : "";

            string dataQuery;
            string countQuery;

            if (sum)
            {
                dataQuery = $@"
                    WITH SumData AS (
                        SELECT 
                            {(string.IsNullOrEmpty(sumaQuery) ? "" : $"{sumaQuery},")}
                            SUM(Cantidad) AS Cantidad,
                            SUM(CostoTotal) AS Costo
                        {baseQuery}
                        {whereQuery}
                        {(string.IsNullOrEmpty(sumaQuery) ? "" : $"GROUP BY {sumaQuery}")}
                    )
                    SELECT * FROM SumData
                    ORDER BY Costo DESC
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

                return Ok(new
                {
                    TotalRecords = totalRecords,
                    TotalPages = (int)Math.Ceiling(totalRecords / (double)pageSize),
                    Page = page,
                    PageSize = pageSize,
                    Data = results
                });
            }
            catch (Exception ex)
            {
                return HandleException(ex, combinedQuery);
            }
        }

        private void BuildFilters(ReporteriaRequest request, List<string> whereClauses, List<SqlParameter> parameters,
            Dictionary<string, int> parameterCounters, string tableName)
        {
            var fechaEmisionParams = request.Filtros.Where(f => f.Key == "FechaEmision").ToList();
            bool fechaRangeProcessed = false;

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
                    if (!parameterCounters.ContainsKey(filter.Key))
                        parameterCounters[filter.Key] = 0;
                    else
                        parameterCounters[filter.Key]++;

                    var paramName = $"@Codigo_{parameterCounters[filter.Key]}";
                    whereClauses.Add($"Articulo IN (SELECT Articulo FROM [LOCAL_TC032391E].[dbo].[{tableName}] WHERE Codigo {operatorClause} {paramName})");

                    parameters.Add(new SqlParameter(paramName, operatorClause == "LIKE" ? $"%{filter.Value}%" : filter.Value));
                }
                else if (!string.IsNullOrWhiteSpace(filter.Value))
                {
                    var column = filter.Key;
                    if (!parameterCounters.ContainsKey(column))
                        parameterCounters[column] = 0;
                    else
                        parameterCounters[column]++;

                    var paramName = $"@{column}_{parameterCounters[column]}";
                    whereClauses.Add($"{column} {operatorClause} {paramName}");

                    parameters.Add(new SqlParameter(paramName, operatorClause == "LIKE" ? $"%{filter.Value}%" : filter.Value));
                }
            }
        }
    }
}