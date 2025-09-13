using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Caching.Memory;
using MyApiProject.Models;

namespace MyApiProject.Controllers.pickUp
{
    [ApiExplorerSettings(GroupName = "pickUp")]
    [Route("api/v1/pickUp")]
    [ApiController]
    public partial class PickUpController : BaseController
    {
        private readonly IMemoryCache _memoryCache;

        public PickUpController(IConfiguration configuration, IMemoryCache memoryCache) : base(configuration, memoryCache)
        {
            _memoryCache = memoryCache;
        }

        // ✅ Consulta pickUp (sin ID)
        /* [Authorize] */
        [HttpGet("consultar")]
        public async Task<IActionResult> ConsultarPickUp()
        {
            /*int userId;
            try { userId = ObtenerUsuarioId(); } 
            catch (UnauthorizedAccessException ex) { return Unauthorized(new { Message = ex.Message }); }*/

            string cacheKey = $"pickUp_all";
            if (_memoryCache.TryGetValue(cacheKey, out List<Dictionary<string, object>> cachedResults))
                return Ok(cachedResults);

            string query = @"SELECT TOP(10) 
                cb.Codigo,
                cb.Cuenta,
                art.Grupo,
                art.Descripcion1 AS Nombre,
                lpu.Unidad,
                lpu.Precio AS PrecioRegular,
                au.Unidad AS UnidadFactor,
                au.Factor,
                inv.TotalInventario
             FROM 
                [TC032841E].[dbo].[CB] AS cb
                INNER JOIN [TC032841E].[dbo].[Art] AS art 
                    ON cb.Cuenta = art.Articulo
                INNER JOIN [TC032841E].[dbo].[ListaPreciosDUnidad] AS lpu
                    ON art.Articulo = lpu.Articulo
                    AND cb.Unidad = lpu.Unidad
                    AND lpu.Lista = '(Precio Lista)'
                INNER JOIN [TC032841E].[dbo].[ArtUnidad] AS au
                    ON art.Articulo = au.Articulo
                    AND lpu.Unidad = au.Unidad  
                INNER JOIN (
                    SELECT 
                        Articulo,
                        SUM(DispMenosApartado)  as TotalInventario
                    FROM ArtDisponible 
                    WHERE 
                        Almacen = 'ALMMAYO'
                    GROUP BY Articulo
                ) AS inv 
                    ON art.Articulo = inv.Articulo";

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
        /* [Authorize] */
        [HttpGet("consultar/{id}")]
        public async Task<IActionResult> ConsultarPorId(int id)
        {
            /*int userId;
            try { userId = ObtenerUsuarioId(); } 
            catch (UnauthorizedAccessException ex) { return Unauthorized(new { Message = ex.Message }); }*/

            string cacheKey = $"pickUp_{id}";
            if (_memoryCache.TryGetValue(cacheKey, out List<Dictionary<string, object>> cachedResults))
                return Ok(cachedResults);

            string query = @"SELECT TOP(10) 
                cb.Codigo,
                cb.Cuenta,
                art.Grupo,
                art.Descripcion1 AS Nombre,
                lpu.Unidad,
                lpu.Precio AS PrecioRegular,
                au.Unidad AS UnidadFactor,
                au.Factor,
                inv.TotalInventario
             FROM 
                [TC032841E].[dbo].[CB] AS cb
                INNER JOIN [TC032841E].[dbo].[Art] AS art 
                    ON cb.Cuenta = art.Articulo
                INNER JOIN [TC032841E].[dbo].[ListaPreciosDUnidad] AS lpu
                    ON art.Articulo = lpu.Articulo
                    AND cb.Unidad = lpu.Unidad
                    AND lpu.Lista = '(Precio Lista)'
                INNER JOIN [TC032841E].[dbo].[ArtUnidad] AS au
                    ON art.Articulo = au.Articulo
                    AND lpu.Unidad = au.Unidad  
                INNER JOIN (
                    SELECT 
                        Articulo,
                        SUM(DispMenosApartado)  as TotalInventario
                    FROM ArtDisponible 
                    WHERE 
                        Almacen = 'ALMMAYO'
                    GROUP BY Articulo
                ) AS inv 
                    ON art.Articulo = inv.Articulo
             WHERE cb.Codigo = @ID";

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

        /* [Authorize] */
        [HttpPost("consultar/filtros")]
        public async Task<IActionResult> ConsultarPickUpConFiltros(
            [FromBody] FiltrosRequest request,
            [FromQuery] string listaPrecio = "(Precio Lista)",
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 10)
        {
            if (page <= 0) page = 1;
            if (pageSize <= 0) pageSize = 10;

            int offset = (page - 1) * pageSize;

            // Construir la cláusula SELECT y GROUP BY
            var (selectClause, groupByClause) = BuildSelectClause(request);
            var baseQuery = @"
                FROM [TC032841E].[dbo].[CB] AS cb
                INNER JOIN [TC032841E].[dbo].[Art] AS art 
                    ON cb.Cuenta = art.Articulo
                INNER JOIN [TC032841E].[dbo].[ListaPreciosDUnidad] AS lpu
                    ON art.Articulo = lpu.Articulo
                    AND cb.Unidad = lpu.Unidad
                    AND lpu.Lista = @ListaPrecio
                INNER JOIN [TC032841E].[dbo].[ArtUnidad] AS au
                    ON art.Articulo = au.Articulo
                    AND lpu.Unidad = au.Unidad  
                INNER JOIN (
                    SELECT 
                        Articulo,
                        SUM(DispMenosApartado)  as TotalInventario
                    FROM ArtDisponible 
                    WHERE 
                        Almacen =
                        CASE 
                            WHEN @ListaPrecio = '(Precio Lista)' THEN 'ALMMAYO'
                            WHEN @ListaPrecio = '(Precio 4)' THEN 'ALMGPE'
                            WHEN @ListaPrecio = '(Precio 3)' THEN 'ALMPALM'
                            WHEN @ListaPrecio = '(Precio 2)' THEN 'ALMTESTE'
                            ELSE 'ALMMAYO'
                        END
                    GROUP BY Articulo
                ) AS inv 
                    ON art.Articulo = inv.Articulo";

            var whereClauses = new List<string>();
            var parameters = new List<SqlParameter>();
            var parameterCounters = new Dictionary<string, int>();

            // Procesar filtros usando el método de BaseController
            BuildFilters(request, whereClauses, parameters, parameterCounters);

            // Agrupar condiciones usando el método de BaseController
            var groupedWhereClauses = AgruparCondiciones(whereClauses);

            var whereQuery = groupedWhereClauses.Any()
                ? $"WHERE {string.Join(" AND ", groupedWhereClauses)}"
                : "";

            // Construir ORDER BY usando el método de BaseController
            string orderByClause = BuildOrderByClause(request);

            // Query para contar - usar método simple ya que no hay GROUP BY issues
            var countQuery = $@"SELECT COUNT(*) AS TotalRegistros {baseQuery} {whereQuery}";

            // Construir query principal
            var paginatedQuery = $@"
                SELECT {selectClause}
                {baseQuery} {whereQuery}
                {groupByClause}
                {orderByClause}
                OFFSET @Offset ROWS FETCH NEXT @PageSize ROWS ONLY";

            // Agregar parámetro de lista de precios
            parameters.Add(new SqlParameter("@ListaPrecio", listaPrecio));

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

                // Crear clave de caché única
                var validFiltros = request.Filtros
                    .Where(f => !string.IsNullOrWhiteSpace(f.Key) && !string.IsNullOrWhiteSpace(f.Value))
                    .ToList();

                var filtrosCacheKey = string.Join("_", validFiltros
                    .Select(f => $"{f.Key}_{f.Value}_{f.Operator}"));

                var cacheKey = $"pickUp_filtros_{filtrosCacheKey}_page{page}_size{pageSize}_lista{listaPrecio}";
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
                return HandleException(ex, $"{countQuery}; {paginatedQuery}");
            }
        }
    }
}