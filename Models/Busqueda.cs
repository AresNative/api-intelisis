namespace MyApiProject.Models
{
    public class BusquedaParams
    {
        public string? Key { get; set; }
        public string? Value { get; set; }
        public string? Operator { get; set; }  // Opcional: puedes usar operadores como 'like', '=', '>=', etc.
    }
    public class SumaParams
    {
        public string? Key { get; set; }
    }
    public class SumaAsParams
    {
        public string? Key { get; set; }
        public string? Alias { get; set; }  // Alias para la suma, por ejemplo: "SUM(Cantidad) AS TotalCantidad"
    }
    public class OrderParams
    {
        public string? Key { get; set; }
        public string? Direction { get; set; }  // "ASC" o "DESC"
    }

}