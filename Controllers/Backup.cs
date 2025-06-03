using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using System.IO.Compression;
using System.Text;
using System.Data;

namespace MyApiProject.Controllers
{
    [Route("api/[controller]")]
    public class BackupController : BaseController
    {
        private readonly string _backupPath = Path.Combine(Path.GetTempPath(), "TableBackups");

        public BackupController(IConfiguration configuration) : base(configuration, null)
        {
        }

        [HttpGet("create-and-download")]
        public async Task<IActionResult> CreateAndDownloadBackup([FromQuery] string tables)
        {
            try
            {
                // Validar parámetro
                if (string.IsNullOrWhiteSpace(tables))
                {
                    return BadRequest("Debe especificar las tablas en el parámetro 'tables' (separadas por comas)");
                }

                // Procesar lista de tablas
                var tablesToBackup = tables.Split(',', StringSplitOptions.RemoveEmptyEntries)
                    .Select(t => t.Trim())
                    .ToList();

                // Validar existencia de tablas
                using (var connection = await OpenConnectionAsync())
                {
                    var invalidTables = new List<string>();
                    foreach (var tableName in tablesToBackup.ToList())
                    {
                        var checkQuery = "SELECT COUNT(*) FROM sys.tables WHERE object_id = OBJECT_ID(@TableName)";
                        using (var cmd = new SqlCommand(checkQuery, connection))
                        {
                            cmd.Parameters.AddWithValue("@TableName", tableName);
                            if ((int)await cmd.ExecuteScalarAsync() == 0)
                            {
                                invalidTables.Add(tableName);
                                tablesToBackup.Remove(tableName);
                            }
                        }
                    }

                    if (invalidTables.Any())
                    {
                        return BadRequest($"Tablas no válidas: {string.Join(", ", invalidTables)}");
                    }
                }

                // Mantener estructura original desde aquí
                Directory.CreateDirectory(_backupPath);

                string backupFileName = $"TableBackup_{DateTime.Now:yyyyMMddHHmmss}.sql";
                string zipFileName = $"TableBackup_{DateTime.Now:yyyyMMddHHmmss}.zip";

                string backupFilePath = Path.Combine(_backupPath, backupFileName);
                string zipFilePath = Path.Combine(_backupPath, zipFileName);

                using (var connection = await OpenConnectionAsync())
                {
                    var scriptBuilder = new StringBuilder();

                    foreach (var tableName in tablesToBackup)
                    {
                        scriptBuilder.AppendLine($"-- Estructura de la tabla {tableName}");
                        scriptBuilder.AppendLine(await GetTableSchemaAsync(connection, tableName));
                        scriptBuilder.AppendLine();

                        scriptBuilder.AppendLine($"-- Datos de la tabla {tableName}");
                        scriptBuilder.AppendLine(await GetTableDataAsync(connection, tableName));
                        scriptBuilder.AppendLine();
                    }

                    await System.IO.File.WriteAllTextAsync(backupFilePath, scriptBuilder.ToString());
                }

                using (var zip = ZipFile.Open(zipFilePath, ZipArchiveMode.Create))
                {
                    zip.CreateEntryFromFile(backupFilePath, backupFileName);
                }

                System.IO.File.Delete(backupFilePath);

                var fileStream = new FileStream(zipFilePath, FileMode.Open, FileAccess.Read);
                var result = new FileStreamResult(fileStream, "application/zip")
                {
                    FileDownloadName = zipFileName
                };

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
            var query = @"
                SELECT definition 
                FROM sys.sql_modules 
                WHERE object_id = OBJECT_ID(@TableName)";

            using (var command = new SqlCommand(query, connection))
            {
                command.Parameters.AddWithValue("@TableName", tableName);
                var schema = await command.ExecuteScalarAsync();
                return schema?.ToString() ?? $"-- No se encontró la estructura de la tabla {tableName}";
            }
        }

        private async Task<string> GetTableDataAsync(SqlConnection connection, string tableName)
        {
            var quotedTable = new SqlCommandBuilder().QuoteIdentifier(tableName);
            var query = $"SELECT * FROM {quotedTable}";

            using (var command = new SqlCommand(query, connection))
            using (var reader = await command.ExecuteReaderAsync())
            {
                var dataBuilder = new StringBuilder();

                while (await reader.ReadAsync())
                {
                    var rowValues = new List<string>();
                    for (int i = 0; i < reader.FieldCount; i++)
                    {
                        var value = reader[i];
                        rowValues.Add(value != DBNull.Value ? $"'{value.ToString().Replace("'", "''")}'" : "NULL");
                    }
                    dataBuilder.AppendLine($"INSERT INTO {quotedTable} VALUES ({string.Join(", ", rowValues)});");
                }

                return dataBuilder.ToString();
            }
        }
    }
}