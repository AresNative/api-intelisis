using MyApiProject.Models;

public class ReporteriaRequest
{
    public List<BusquedaParams> Filtros { get; set; } = new();
    public List<SumaParams> Selects { get; set; } = new();
    /* public List<SumaAsParams> sumaAs { get; set; } = new(); */
    public List<OrderParams> Order { get; set; } = new();
}