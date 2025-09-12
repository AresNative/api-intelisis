namespace MyApiProject.Models
{
    public class BusquedaParams
    {
        public string? Key { get; set; }
        public string? Value { get; set; }
        public string? Operator { get; set; }
    }

    public class SelectParams
    {
        public string? Key { get; set; }
    }

    public class SumaParams
    {
        public string? Key { get; set; }
        public string? Alias { get; set; }
    }

    public class OrderParams
    {
        public string? Key { get; set; }
        public string? Direction { get; set; }
    }

    public class FiltrosRequest
    {
        public List<BusquedaParams> Filtros { get; set; } = new();
        public List<object> Selects { get; set; } = new(); // Cambiado a List<object>
        public List<OrderParams> Order { get; set; } = new();
    }
}