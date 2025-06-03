// Controllers/BaseController.cs
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Caching.Memory;

namespace MyApiProject.Controllers
{
    public abstract class BaseController : ControllerBase
    {
        private readonly string _connectionString;
        protected readonly IMemoryCache _cache;

        public BaseController(IConfiguration configuration, IMemoryCache cache)
        {
            _connectionString = configuration.GetConnectionString("DefaultConnection")
                                ?? throw new InvalidOperationException("Cadena de conexión 'DefaultConnection' no encontrada");
            _cache = cache;
        }

        protected async Task<SqlConnection> OpenConnectionAsync()
        {
            var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync();
            return connection;
        }

        protected IActionResult HandleException(Exception ex, string? query = null)
        {
            if (ex is OperationCanceledException || ex is TaskCanceledException)
                return StatusCode(499, new { Message = "Solicitud cancelada por el cliente" });

            var sanitizedMessage = ex.Message.Replace("\r", "").Replace("\n", " ");
            var sanitizedQuery = query?.Replace("\r", "").Replace("\n", " ");

            return StatusCode(500, new
            {
                Message = $"Error: {sanitizedMessage}",
                Query = sanitizedQuery
            });
        }

        protected IActionResult HandleException(Exception ex, int statusCode)
        {
            if (ex is OperationCanceledException || ex is TaskCanceledException)
                return StatusCode(499, "Solicitud cancelada por el cliente");

            return StatusCode(statusCode, new { Message = $"Error: {ex.Message}" });
        }
    }
}
