public class OfertaHeaderDTO
{
    public string Mov { get; set; } // Ej: "Oferta", "Combo"
    public DateTime FechaEmision { get; set; } = DateTime.Now;
    public string Concepto { get; set; }
    public string Usuario { get; set; } // Usuario que crea
    public decimal? MontoMinimo { get; set; }
    public string Moneda { get; set; } = "MXN";
    // ... otros campos según necesidad
}

public class OfertaDetailDTO
{
    public string Articulo { get; set; }
    public decimal Cantidad { get; set; } = 1;
    public decimal? Porcentaje { get; set; } // % descuento
    public decimal? Precio { get; set; } // Precio directo
    public bool EsObsequio { get; set; } // Si es artículo gratis
    // ... otros campos
}

public class OfertaRequestDTO
{
    public OfertaHeaderDTO Header { get; set; }
    public List<OfertaDetailDTO> Details { get; set; }
}