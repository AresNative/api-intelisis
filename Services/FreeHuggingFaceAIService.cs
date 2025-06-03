// Services/FreeHuggingFaceAIService.cs
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace MyApiProject.Services
{
    public class FreeHuggingFaceAIService : IAIService
    {
        private readonly HttpClient _http;
        private readonly string _hfToken;
        private readonly string _chatEndpoint = "https://router.huggingface.co/fireworks-ai/inference/v1/chat/completions";
        private readonly string _modelId = "accounts/fireworks/models/deepseek-r1-0528";

        public FreeHuggingFaceAIService(HttpClient http, IConfiguration cfg)
        {
            _http = http;
            _hfToken = cfg["HUGGINGFACE_TOKEN"]
                     ?? cfg.GetSection("HuggingFace")["Token"]
                     ?? throw new InvalidOperationException("No se ha cargado HUGGINGFACE_TOKEN");

            _http.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", _hfToken);
        }

        public async Task<string> DescribeColumnAsync(string tableName, string columnName)
        {
            // Preparamos el mensaje de usuario
            var userContent = $"Proporciona en español una breve descripción para la columna '{columnName}' " +
                              $"de la tabla '{tableName}' en una base de datos empresarial.";

            // Montamos el payload de chat
            var payload = new
            {
                model = _modelId,
                stream = false,   // true si quieres streaming; aquí false para simplificar
                messages = new[]
                {
                    new { role = "user", content = userContent }
                }
            };

            var json = JsonSerializer.Serialize(payload);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            // Enviamos la petición
            var resp = await _http.PostAsync(_chatEndpoint, content);
            var body = await resp.Content.ReadAsStringAsync();
            Console.WriteLine($"HF Chat → {(int)resp.StatusCode}: {body}");

            if (resp.StatusCode == HttpStatusCode.Forbidden)
                throw new InvalidOperationException("Acceso denegado (403). Revisa tu token y scopes.");
            if (resp.StatusCode == (HttpStatusCode)429)
                throw new InvalidOperationException("Límite de tasa excedido (429). Prueba más tarde.");
            if (resp.StatusCode == HttpStatusCode.NotFound)
                throw new InvalidOperationException("Endpoint no encontrado (404). Revisa la URL.");

            resp.EnsureSuccessStatusCode();

            // Parseamos la respuesta de chat completions
            using var doc = JsonDocument.Parse(body);
            // Asumimos la estructura: { choices: [ { message: { content: "..." } } ] }
            var contentText = doc.RootElement
                                 .GetProperty("choices")[0]
                                 .GetProperty("message")
                                 .GetProperty("content")
                                 .GetString()!
                                 .Trim();

            // Devolvemos solo la primera línea como descripción
            return contentText.Split('\n', StringSplitOptions.RemoveEmptyEntries)[0];
        }
    }
}
