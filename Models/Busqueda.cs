// Models/FiltrosRequest.cs
namespace MyApiProject.Models
{
    // ── Proyección ───────────────────────────────────────────────────────────

    public class SelectItem
    {
        /// <summary>Columna calificada, ej: "INV.Folio" o expresión CASE WHEN completa</summary>
        public string Key { get; set; } = string.Empty;

        /// <summary>Alias en el resultado, ej: "NumeroFactura"</summary>
        public string? Alias { get; set; }
    }

    /// <summary>
    /// Operaciones permitidas: SUM | COUNT | AVG | MIN | MAX | DISTINCT | COUNT DISTINCT
    /// </summary>
    public class AgregacionItem
    {
        /// <summary>Columna o expresión, ej: "INVD.Importe" o "CASE WHEN ..."</summary>
        public string Key { get; set; } = string.Empty;

        /// <summary>SUM | COUNT | AVG | MIN | MAX | DISTINCT | COUNT DISTINCT</summary>
        public string? Operation { get; set; }

        /// <summary>Alias en el resultado, ej: "TotalImporte"</summary>
        public string? Alias { get; set; }
    }

    // ── Filtros ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Filtro individual.
    ///   - BETWEEN:      Value = "valor1 AND valor2"
    ///   - IN / NOT IN:  Value = "v1,v2,v3"
    ///   - TIME_BETWEEN: Value = "08:00:00 AND 17:00:00"
    ///   - CASE_WHEN:    Value = JSON { "when":[{"condition":"...","then":"..."}], "else":"..." }
    ///   - IS NULL / IS NOT NULL: Value puede omitirse
    /// </summary>
    public class BusquedaParams
    {
        public string Key { get; set; } = string.Empty;
        public string Value { get; set; } = string.Empty;

        /// <summary>
        /// = | != | &lt;&gt; | &gt; | &gt;= | &lt; | &lt;= | LIKE |
        /// IN | NOT IN | BETWEEN | NOT BETWEEN |
        /// IS NULL | IS NOT NULL | TIME_BETWEEN | CASE_WHEN
        /// </summary>
        public string? Operator { get; set; } = "=";
    }

    /// <summary>
    /// Grupo de filtros con operador lógico interno.
    /// Los miembros del grupo se unen con OperadorLogico (AND u OR).
    /// Los grupos entre sí siempre se combinan con AND al WHERE principal.
    /// </summary>
    public class GrupoFiltros
    {
        /// <summary>AND | OR — operador entre los filtros dentro del grupo</summary>
        public string OperadorLogico { get; set; } = "AND";

        public List<BusquedaParams> Filtros { get; set; } = new();
    }

    // ── Ordenamiento ─────────────────────────────────────────────────────────

    public class OrderItem
    {
        public string Key { get; set; } = string.Empty;

        /// <summary>ASC | DESC</summary>
        public string Direction { get; set; } = "ASC";
    }

    // ── Request principal ────────────────────────────────────────────────────

    /// <summary>
    /// Request para el endpoint de consulta masiva.
    ///
    /// ESTRUCTURA DE FILTROS (los tres niveles se combinan con AND entre sí):
    ///
    ///   Filtros    → lista plana, todas AND entre sí.
    ///   FiltrosAnd → grupos con OperadorLogico interno (AND u OR dentro del grupo).
    ///   FiltrosOr  → mismo tipo que FiltrosAnd. Misma lógica de procesamiento.
    ///                Existe como nombre semántico separado para organizar el JSON del cliente.
    ///
    /// SQL resultante:
    ///   WHERE [filtros planos]
    ///     AND [grupo1 de FiltrosAnd]
    ///     AND [grupo2 de FiltrosAnd]
    ///     AND [grupo1 de FiltrosOr]
    ///     AND [grupo2 de FiltrosOr]
    /// </summary>
    public class FiltrosRequest
    {
        public List<SelectItem> Selects { get; set; } = new();
        public List<AgregacionItem> Agregaciones { get; set; } = new();

        /// <summary>Condiciones planas, unidas con AND.</summary>
        public List<BusquedaParams> Filtros { get; set; } = new();

        /// <summary>Grupos de filtros con operador lógico interno.</summary>
        public List<GrupoFiltros> FiltrosAnd { get; set; } = new();

        /// <summary>Grupos de filtros con operador lógico interno (semánticamente "OR groups").</summary>
        public List<GrupoFiltros> FiltrosOr { get; set; } = new();

        public List<OrderItem> Order { get; set; } = new();

        /// <summary>
        /// Filtros sobre valores agregados — equivale al HAVING de SQL.
        /// Se aplican DESPUÉS del GROUP BY, por lo que pueden referenciar
        /// aliases de agregaciones (ej: totalCosto, minimoCosto).
        ///
        /// Ejemplo: ver solo proveedores con totalCosto > 10000
        ///   { "Key": "totalCosto", "Operator": ">", "Value": "10000" }
        /// </summary>
        public List<BusquedaParams> Having { get; set; } = new();

        public int Page { get; set; } = 1;
        public int PageSize { get; set; } = 50;
    }
}