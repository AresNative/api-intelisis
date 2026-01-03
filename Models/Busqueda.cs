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
        public string? Alias { get; set; }
    }

    public class AgregacionParams
    {
        public string? Key { get; set; }
        public string? Operation { get; set; }
        public string? Alias { get; set; }
    }

    public class OrderParams
    {
        public string? Key { get; set; }
        public string? Direction { get; set; }
    }

    public class FiltroGrupo
    {
        public List<BusquedaParams> Filtros { get; set; } = new();
        public string? OperadorLogico { get; set; } // "AND" u "OR" dentro del grupo
    }

    public class FiltrosRequest
    {
        public List<BusquedaParams> Filtros { get; set; } = new();
        public List<SelectParams> Selects { get; set; } = new();
        public List<AgregacionParams> Agregaciones { get; set; } = new();
        public List<OrderParams> Order { get; set; } = new();

        // Nuevas propiedades para filtros avanzados
        public List<FiltroGrupo> FiltrosAnd { get; set; } = new(); // Grupos que se unen con AND
        public List<FiltroGrupo> FiltrosOr { get; set; } = new();  // Grupos que se unen con OR
    }
}