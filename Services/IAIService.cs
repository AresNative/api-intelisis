// Services/IAIService.cs
namespace MyApiProject.Services
{
    public interface IAIService
    {
        /// <summary>
        /// Devuelve una descripción natural para la columna dada.
        /// </summary>
        Task<string> DescribeColumnAsync(string tableName, string columnName);
    }
}
