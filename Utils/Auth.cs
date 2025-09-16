using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using MyApiProject.Models;

public class AuthUtils
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<AuthUtils> _logger;
    private readonly HttpClient _httpClient;

    public AuthUtils(IConfiguration configuration, ILogger<AuthUtils> logger, HttpClient httpClient)
    {
        _configuration = configuration;
        _logger = logger;
        _httpClient = httpClient;
    }
    public async Task<bool> IsValidUser(LoginModel login)
    {
        try
        {
            // Configurar la URL de la API externa de autenticación
            var authApiUrl = _configuration["ExternalApis:MercadosLiz:AuthUrl"] ?? "https://api.mercadosliz.com:5230/auth/verify";

            // Preparar los datos para la solicitud
            var loginData = new
            {
                email = login.Email,
                password = login.Password
            };

            var jsonContent = JsonSerializer.Serialize(loginData);
            var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

            // Realizar la solicitud a la API externa
            var response = await _httpClient.PostAsync(authApiUrl, content);

            if (response.IsSuccessStatusCode)
            {
                var responseContent = await response.Content.ReadAsStringAsync();
                var authResponse = JsonSerializer.Deserialize<AuthResponse>(responseContent,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                return authResponse?.Success ?? false;
            }

            _logger.LogWarning($"Autenticación fallida en API externa. Status: {response.StatusCode}");
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error al validar el usuario en la API externa.");
            return false;
        }
    }

    public async Task<ExternalUserInfo?> GetUserInfoFromExternalApi(string email, string accessToken = null)
    {
        try
        {
            var userInfoUrl = _configuration["ExternalApis:MercadosLiz:UserInfoUrl"] ??
                            $"https://api.mercadosliz.com:5230/users/{email}";

            var request = new HttpRequestMessage(HttpMethod.Get, userInfoUrl);

            if (!string.IsNullOrEmpty(accessToken))
            {
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
            }

            var response = await _httpClient.SendAsync(request);

            if (response.IsSuccessStatusCode)
            {
                var responseContent = await response.Content.ReadAsStringAsync();
                var userInfo = JsonSerializer.Deserialize<ExternalUserInfo>(responseContent,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                return userInfo;
            }

            _logger.LogWarning($"Error al obtener información del usuario desde API externa. Status: {response.StatusCode}");
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error al obtener información del usuario desde API externa.");
            return null;
        }
    }
    // Los demás métodos permanecen iguales...
    public string GetEmailFromToken(string token)
    {
        var tokenHandler = new JwtSecurityTokenHandler();
        var jwtToken = tokenHandler.ReadJwtToken(token);

        var claim = jwtToken.Claims.FirstOrDefault(c =>
            c.Type == "sub" || c.Type == ClaimTypes.Name || c.Type.EndsWith("/name"));

        return claim?.Value ?? throw new Exception("El token no contiene un claim de correo.");
    }

    // Clases para deserializar las respuestas de la API externa
    public class AuthResponse
    {
        public bool Success { get; set; }
        public string Token { get; set; }
        public DateTime Expiration { get; set; }
    }

    public class ExternalUserInfo
    {
        public int Id { get; set; }
        public string Email { get; set; }
        public string Nombre { get; set; }
        public string Apellido { get; set; }
        public string Rol { get; set; }
    }
}