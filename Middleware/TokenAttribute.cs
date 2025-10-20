// Attributes/ValidateTokenAttribute.cs
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace MyApiProject.Attributes
{
    public class ValidateTokenAttribute : ActionFilterAttribute
    {
        public override async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
        {
            var token = ExtractTokenFromHeader(context.HttpContext);

            if (string.IsNullOrEmpty(token))
            {
                context.Result = new UnauthorizedObjectResult("Token requerido");
                return;
            }

            if (!await IsTokenValidAsync(token))
            {
                context.Result = new UnauthorizedObjectResult("Token inválido o expirado");
                return;
            }

            await next();
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
                return response.IsSuccessStatusCode;
            }
            catch
            {
                return false;
            }
        }
    }
}