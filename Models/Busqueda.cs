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

    public class AgregacionParams
    {
        public string? Key { get; set; }
        public string? Operation { get; set; } // "SUM", "COUNT", "AVG", "MIN", "MAX", "DISTINCT"
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
        public List<SelectParams> Selects { get; set; } = new(); // Solo para columnas normales
        public List<AgregacionParams> Agregaciones { get; set; } = new(); // Nuevo: para operaciones
        public List<OrderParams> Order { get; set; } = new();
    }
}