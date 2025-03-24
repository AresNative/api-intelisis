using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using System.IO.Compression;
using System.Text;

namespace MyApiProject.Controllers
{
    [Route("api/[controller]")]
    public class BackupController : BaseController
    {
        private readonly string _backupPath = Path.Combine(Path.GetTempPath(), "TableBackups");

        public BackupController(IConfiguration configuration) : base(configuration)
        {
        }

        [HttpGet("create-and-download")]
        public async Task<IActionResult> CreateAndDownloadBackup()
        {
            try
            {
                // Crear directorio temporal si no existe
                Directory.CreateDirectory(_backupPath);

                // Generar nombres de archivo únicos
                string backupFileName = $"TableBackup_{DateTime.Now:yyyyMMddHHmmss}.sql";
                string zipFileName = $"TableBackup_{DateTime.Now:yyyyMMddHHmmss}.zip";

                string backupFilePath = Path.Combine(_backupPath, backupFileName);
                string zipFilePath = Path.Combine(_backupPath, zipFileName);

                // Tablas específicas a respaldar
                var tablesToBackup = new List<string> { "INVD", "inv", "art" }; // Cambia esto por tus tablas

                // Crear el archivo SQL con los datos de las tablas
                using (var connection = await OpenConnectionAsync())
                {
                    var scriptBuilder = new StringBuilder();

                    foreach (var tableName in tablesToBackup)
                    {
                        // Obtener la estructura de la tabla
                        scriptBuilder.AppendLine($"-- Estructura de la tabla {tableName}");
                        scriptBuilder.AppendLine(await GetTableSchemaAsync(connection, tableName));
                        scriptBuilder.AppendLine();

                        // Obtener los datos de la tabla
                        scriptBuilder.AppendLine($"-- Datos de la tabla {tableName}");
                        scriptBuilder.AppendLine(await GetTableDataAsync(connection, tableName));
                        scriptBuilder.AppendLine();
                    }

                    // Guardar el script en un archivo
                    await System.IO.File.WriteAllTextAsync(backupFilePath, scriptBuilder.ToString());
                }

                // Crear archivo ZIP
                using (var zip = ZipFile.Open(zipFilePath, ZipArchiveMode.Create))
                {
                    zip.CreateEntryFromFile(backupFilePath, backupFileName);
                }

                // Limpiar archivos temporales
                System.IO.File.Delete(backupFilePath);

                // Devolver el archivo ZIP
                var fileStream = new FileStream(zipFilePath, FileMode.Open, FileAccess.Read);
                var result = new FileStreamResult(fileStream, "application/zip")
                {
                    FileDownloadName = zipFileName
                };

                // Eliminar el ZIP después de enviarlo
                Response.OnCompleted(() =>
                {
                    fileStream.Dispose();
                    System.IO.File.Delete(zipFilePath);
                    return Task.CompletedTask;
                });

                return result;
            }
            catch (Exception ex)
            {
                return HandleException(ex, $"Backup failed. Details: {ex.InnerException?.Message}");
            }
        }

        private async Task<string> GetTableSchemaAsync(SqlConnection connection, string tableName)
        {
            var query = $@"
                SELECT definition
                FROM sys.sql_modules
                WHERE object_id = OBJECT_ID('{tableName}')";

            using (var command = new SqlCommand(query, connection))
            {
                var schema = await command.ExecuteScalarAsync();
                return schema?.ToString() ?? $"-- No se encontró la estructura de la tabla {tableName}";
            }
        }

        private async Task<string> GetTableDataAsync(SqlConnection connection, string tableName)
        {
            var query = $@"
                SELECT *
                FROM {tableName}";

            using (var command = new SqlCommand(query, connection))
            using (var reader = await command.ExecuteReaderAsync())
            {
                var dataBuilder = new StringBuilder();

                while (await reader.ReadAsync())
                {
                    var rowValues = new List<string>();
                    for (int i = 0; i < reader.FieldCount; i++)
                    {
                        rowValues.Add(reader[i]?.ToString() ?? "NULL");
                    }
                    dataBuilder.AppendLine($"INSERT INTO {tableName} VALUES ({string.Join(", ", rowValues)});");
                }

                return dataBuilder.ToString();
            }
        }
    }
}