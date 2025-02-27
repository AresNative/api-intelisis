# API de Administracion

Basado en la BD de intelisis

## Links

[![portfolio](https://img.shields.io/badge/my_portfolio-000?style=for-the-badge&logo=ko-fi&logoColor=white)](https://eusebio.dev/)

## Environment Variables 📌

El proyecto al ser en .net 8 no existe un archivo .env pero si existe seccion de configuracion en -> [appsettings.json](https://github.com/AresNative/api/blob/v2/appsettings.json)

"DefaultConnection": `CONTECTION_BD_SQLSERVER`

_key inicial para el uso de JWT_ "Key" : `API_KEY`

_key PUBLICA para JWT no obligatoria_ "PublicKey": `ANOTHER_API_KEY`

## Características

- Endpoints con paginado dinamico
- Filtros escalables
- Modelos descriptivos
- Insterts escalables

## Tecnologias

**Client:** NextJS, Ionic, React, Redux

**Server:** .NET 8, NodeJs, Express

## Usado en

Este proyecto lo utilizan las siguientes paginas:

- [mercadosliz.com](https://mercadosliz.com)

- [admin.mercadosliz.com](https://admin.mercadosliz.com)

## Comentarios

Si tiene algún comentario, comuníquese con nosotros en sistemas02@mercadosliz.com

## Soporte

Para recibir asistencia, envíe un correo electrónico a sistemas02@mercadosliz.com o únase a nuestro canal de Slack.

## Instalacion

Clonar proyecto

```bash
  git clone https://github.com/AresNative/api.git --depth=1
```

Ir a la direccion creada

```bash
  cd api
```

Limpiar dependencias

```bash
  dotnet clean
```

Instalar dependencias

```bash
  dotnet build
```

Iniciar api

```bash
  dotnet run
```

## Publicar

```bash
  dotnet publish  -o ./publish
```

## Uso de API

#### Gets

```http
  GET /api/v1/glosarios/glosario-compras
```

| Parameter | Type     | Description                |
| :-------- | :------- | :------------------------- |
| `api_key` | `string` | **Required**. Your API key |

#### Get item

```http
  POST /api/api/v1/reporteria/compras
```

| Parameter  | Type     | Description                                                  |
| :--------- | :------- | :----------------------------------------------------------- |
| `api_key`  | `string` | **Required**. Your API key                                   |
| `sum`      | `bool`   | false/true                                                   |
| `page`     | `number` | **Required**. Pagina en la que se encuentra                  |
| `pageSize` | `numbre` | **Required**. Items por pagina                               |
| Parameter  | Type     | Description                                                  |
| **body**   | :------- | :-------------------------                                   |
| `filtros`  | `any`    | [ {"key": "string", "value": "string","operator": "string"}] |
| `sumas`    | `any`    | [ {"key": "string"}]                                         |

#### add(num1, num2)

Takes two numbers and returns the sum.

## Ejemplos/Uso

- **Post** - _Insercion o consulta dinamica_ - [ejemplo funcional](https://github.com/AresNative/api/blob/v2/Controllers/Reports/ComprasController.cs)

```javascript
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;

namespace MyApiProject.Controllers
{
    public partial class Posts : BaseController
    {
        public Posts(IConfiguration configuration) : base(configuration) { }
        [HttpPost("")]
        public async Task<IActionResult> UsePosts(
            [FromBody] PostsRequest request,
            [FromQuery] bool sum = false,
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 10)
        {
            if (page <= 0) page = 1;
            if (pageSize <= 0) pageSize = 10;

            int offset = (page - 1) * pageSize;

            var baseQuery = @"
            FROM [].[].[]";

            var whereClauses = new List<string>();
            var sumaClauses = new List<string>();
            var parameters = new List<SqlParameter>();
            var parameterCounters = new Dictionary<string, int>();

            // Procesar filtros
            var fechaEmisionParams = request.Filtros.Where(f => f.Key == "").ToList();
            bool fechaRangeProcessed = false;

            // Manejo de rango de fechas si hay exactamente dos filtros
            if (fechaEmisionParams.Count == 2)
            {
                var minFecha = fechaEmisionParams.FirstOrDefault(f => f.Operator == ">=");
                var maxFecha = fechaEmisionParams.FirstOrDefault(f => f.Operator == "<=");

                if (minFecha != null && maxFecha != null)
                {
                    whereClauses.Add(" BETWEEN @ AND @");
                    parameters.Add(new SqlParameter("@", DateTime.Parse(minFecha.Value)));
                    parameters.Add(new SqlParameter("@", DateTime.Parse(maxFecha.Value)));
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
                    whereClauses.Add($"{operatorClause} {uniqueParameterName})");

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

            // Procesar sumas (sin cambios)
            foreach (var suma in request.Sumas)
            {
                if (!string.IsNullOrWhiteSpace(suma.Key))
                {
                    sumaClauses.Add(suma.Key);
                }
            }

            // Agrupar condiciones (sin cambios)
            var groupedConditions = whereClauses
                .Select(c => new
                {
                    Key = c.Split(' ')[0],
                    Condition = c
                })
                .GroupBy(x => x.Key)
                .Select(g => g.Count() > 1
                    ? $"({string.Join(" OR ", g.Select(x => x.Condition))})"
                    : g.First().Condition)
                .ToList();

            var whereQuery = groupedConditions.Any()
                ? $"WHERE {string.Join(" AND ", groupedConditions)}"
                : "";

            var sumaQuery = sumaClauses.Any() ? $"{string.Join(", ", sumaClauses)}" : "";

            // Resto del código sin cambios (countQuery, paginatedQuery)
            var countQuery = sum ? $@"" : $@"";

            var paginatedQuery = sum ? $@"" : $@"";
            //Console.Write(paginatedQuery);
            try
            {
                await using var connection = await OpenConnectionAsync();

                // Total records (sin cambios)
                var countCommandParameters = parameters
                    .Select(p => new SqlParameter(p.ParameterName, p.Value))
                    .ToList();

                await using var countCommand = new SqlCommand(countQuery, connection);
                countCommand.Parameters.AddRange(countCommandParameters.ToArray());
                var totalRecords = (int)await countCommand.ExecuteScalarAsync();

                // Paginated data (sin cambios)
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
                return HandleException(ex, paginatedQuery);
            }
        }
    }
}
```

- **Get** - _consulta estatica_ - [ejemplo funcional](https://github.com/AresNative/api/blob/v2/Controllers/Glosarios/GlosarioCompras.cs)

```javascript
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;

namespace MyApiProject.Controllers
{
    public partial class Gets : BaseController
    {
        public Gets(IConfiguration configuration) : base(configuration) { }
        [HttpGet("")]
        public async Task<IActionResult> UseGet()
        {
            try
            {
                var glosario = new List<Dictionary<string, object>>();

                // Diccionario de descripciones personalizadas

                await using var connection = await OpenConnectionAsync();
                await using var command = new SqlCommand(@"
                    SELECT TOP 1 *
                    FROM [].[].[]
                ", connection);

                await using var reader = await command.ExecuteReaderAsync();
                var schemaTable = reader.GetSchemaTable();

                for (int i = 0; i < reader.FieldCount; i++)
                {
                    string columnName = reader.GetName(i);
                    var columnMetadata = schemaTable.Rows[i];

                    var columna = new Dictionary<string, object>
                        {

                        };

                    glosario.Add(columna);
                }

                return Ok(glosario);
            }
            catch (Exception ex)
            {
                return HandleException(ex, "");
            }
        }
    }
}
```
