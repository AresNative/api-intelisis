// Middleware/TokenValidationMiddleware.cs
namespace MyApiProject.Middleware
{
    public class TokenValidationMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly ILogger<TokenValidationMiddleware> _logger;

        public TokenValidationMiddleware(RequestDelegate next, ILogger<TokenValidationMiddleware> logger)
        {
            _next = next;
            _logger = logger;
        }

        public async Task InvokeAsync(HttpContext context)
        {
            // Excluir endpoints públicos o de autenticación
            if (IsPublicEndpoint(context.Request.Path))
            {
                await _next(context);
                return;
            }

            var token = ExtractTokenFromHeader(context);

            if (string.IsNullOrEmpty(token))
            {
                context.Response.StatusCode = 401;
                await context.Response.WriteAsync("Token requerido");
                return;
            }

            if (!await IsTokenValidAsync(token))
            {
                context.Response.StatusCode = 401;
                await context.Response.WriteAsync("Token inválido o expirado");
                return;
            }

            // Token válido, continuar
            await _next(context);
        }

        private bool IsPublicEndpoint(PathString path)
        {
            var publicPaths = new[]
            {
                "/api/auth/login",
                "/api/auth/register",
                "/swagger",
                "/health"
            };

            return publicPaths.Any(publicPath => path.StartsWithSegments(publicPath));
        }

        private string? ExtractTokenFromHeader(HttpContext context)
        {
            var authHeader = context.Request.Headers["Authorization"].FirstOrDefault();
            return authHeader?.StartsWith("Bearer ") == true
                ? authHeader.Substring("Bearer ".Length).Trim()
                : null;
        }

        private async Task<bool> IsTokenValidAsync(string token)
        {
            try
            {
                using var httpClient = new HttpClient();
                httpClient.Timeout = TimeSpan.FromSeconds(10);

                var request = new HttpRequestMessage(HttpMethod.Get, "https://api.mercadosliz.com:5231/api/Auth/verify");
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

                var response = await httpClient.SendAsync(request);

                return response.IsSuccessStatusCode; // 200 = válido, 401 = inválido
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error validando token en middleware");
                return false;
            }
        }
    }
}