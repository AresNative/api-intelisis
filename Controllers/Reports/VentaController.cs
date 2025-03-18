using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using System.Data;

namespace MyApiProject.Controllers
{
    public partial class Reporteria : BaseController
    {
        [HttpPost("api/v1/reporteria/ventas")]
        public async Task<IActionResult> ObtenerVentas(
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

            const string baseQuery = "FROM [LOCAL_TC032391E].[dbo].[Temp_VentasReport]";

            var whereClauses = new List<string>();
            var sumaClauses = new List<string>();
            var parameters = new List<SqlParameter>();
            var parameterCounters = new Dictionary<string, int>();

            BuildFilters(request, whereClauses, parameters, parameterCounters, "Temp_VentasReport");
            foreach (var suma in request.Selects)
            {
                if (!string.IsNullOrWhiteSpace(suma.Key))
                    sumaClauses.Add(suma.Key);
            }

            var condicionesAgrupadas = AgruparCondiciones(whereClauses);
            string whereQuery = condicionesAgrupadas.Any() ? $"WHERE {string.Join(" AND ", condicionesAgrupadas)}" : "";
            string sumaQuery = sumaClauses.Any() ? string.Join(", ", sumaClauses) : "";

            string dataQuery;
            string countQuery = $"SELECT COUNT(*) AS TotalRegistros {baseQuery} {whereQuery}";

            if (sum)
            {
                dataQuery = $@"
                    SELECT {(string.IsNullOrEmpty(sumaQuery) ? "" : $"{sumaQuery},")}
                           SUM(Cantidad) AS Cantidad, SUM(ImporteTotal) AS Importe
                    {baseQuery} {whereQuery}
                    {(string.IsNullOrEmpty(sumaQuery) ? "" : $"GROUP BY {sumaQuery}")}
                    ORDER BY Importe DESC
                    OFFSET @Offset ROWS FETCH NEXT @PageSize ROWS ONLY";
            }
            else if (distinct)
            {
                dataQuery = $@"
                    SELECT DISTINCT {(string.IsNullOrEmpty(sumaQuery) ? "*" : sumaQuery)}
                    {baseQuery} {whereQuery}
                    ORDER BY {(string.IsNullOrEmpty(sumaQuery) ? "FechaEmision" : sumaQuery)} DESC
                    OFFSET @Offset ROWS FETCH NEXT @PageSize ROWS ONLY";
            }
            else
            {
                dataQuery = $@"
                    SELECT {(string.IsNullOrEmpty(sumaQuery) ? "*" : sumaQuery)}
                    {baseQuery} {whereQuery}
                    ORDER BY FechaEmision DESC
                    OFFSET @Offset ROWS FETCH NEXT @PageSize ROWS ONLY";
            }

            parameters.Add(new SqlParameter("@Offset", offset));
            parameters.Add(new SqlParameter("@PageSize", pageSize));

            try
            {
                await using var connection = await OpenConnectionAsync();

                // Ejecutar la consulta de conteo primero
                int totalRecords = 0;
                await using (var countCommand = new SqlCommand(countQuery, connection))
                {
                    foreach (var param in parameters)
                    {
                        countCommand.Parameters.Add(new SqlParameter(param.ParameterName, param.Value));
                    }
                    totalRecords = Convert.ToInt32(await countCommand.ExecuteScalarAsync());
                }

                var results = new List<Dictionary<string, object>>();
                await using (var dataCommand = new SqlCommand(dataQuery, connection))
                {
                    foreach (var param in parameters)
                    {
                        dataCommand.Parameters.Add(new SqlParameter(param.ParameterName, param.Value));
                    }

                    await using var reader = await dataCommand.ExecuteReaderAsync(CommandBehavior.SequentialAccess);
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
                return HandleException(ex, dataQuery);
            }


        }
    }
}