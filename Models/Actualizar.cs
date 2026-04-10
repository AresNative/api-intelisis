// Models/ActualizarRequest.cs
using MyApiProject.Models;
using Newtonsoft.Json.Linq;

namespace MyApiProject.Models
{
    /// <summary>
    /// Request para el endpoint PUT /update/{tabla}.
    ///
    /// Filtros → lista plana de condiciones AND para el WHERE.
    /// Data    → objeto JSON libre con los campos a actualizar.
    ///
    /// Ejemplo:
    /// {
    ///   "Filtros": [
    ///     { "Key": "id", "Operator": "=", "Value": "42" }
    ///   ],
    ///   "Data": {
    ///     "Status": "A",
    ///     "FechaModificacion": "2024-06-01"
    ///   }
    /// }
    /// </summary>
    public class ActualizarRequest
    {
        /// <summary>Condiciones del WHERE. Todas se unen con AND.</summary>
        public List<BusquedaParams> Filtros { get; set; } = new();

        /// <summary>Campos y valores a actualizar (SET clause).</summary>
        public JObject? Data { get; set; }
    }
}