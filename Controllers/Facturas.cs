using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using System.ServiceModel;

namespace MyApiProject.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class ConsultaCFDIController : BaseController
    {
        public ConsultaCFDIController(IConfiguration configuration) : base(configuration)
        {
        }

        [HttpGet("consultar")]
        public async Task<IActionResult> ConsultarCFDI([FromQuery] string uuid, [FromQuery] string rfcEmisor, [FromQuery] string rfcReceptor, [FromQuery] string totalFactura)
        {
            try
            {
                // Validar parámetros
                if (string.IsNullOrEmpty(uuid) || string.IsNullOrEmpty(rfcEmisor) || string.IsNullOrEmpty(totalFactura))
                {
                    return BadRequest(new { Message = "Los parámetros UUID, RFC Emisor y Total Factura son obligatorios." });
                }

                // Crear la expresión impresa para la consulta
                string expresionImpresa = $"?re={rfcEmisor}&rr={rfcReceptor}&tt={totalFactura}&id={uuid}";

                // Consultar el CFDI
                string resultado = await ConsultarCFDIAsync(expresionImpresa);

                // Guardar el resultado en la base de datos (opcional)
                await GuardarResultadoEnBaseDeDatos(uuid, resultado);

                // Devolver el resultado
                return Ok(new { Resultado = resultado });
            }
            catch (Exception ex)
            {
                // Manejar la excepción utilizando el método de la base
                return HandleException(ex);
            }
        }

        private async Task<string> ConsultarCFDIAsync(string expresionImpresa)
        {
            // URL del servicio de consulta del SAT
            string url = "https://consultaqr.facturaelectronica.sat.gob.mx/ConsultaCFDIService.svc";

            // Crear el binding y el endpoint
            var binding = new BasicHttpBinding(BasicHttpSecurityMode.Transport);
            var endpoint = new EndpointAddress(url);

            // Crear el cliente del servicio
            var client = new ConsultaCFDIServiceClient(binding, endpoint);

            // Crear la solicitud SOAP
            var request = new ConsultaRequest(expresionImpresa);

            // Enviar la solicitud y obtener la respuesta
            var response = await client.ConsultaAsync(request);

            // Devolver el resultado
            return response.ConsultaResult;
        }

        private async Task GuardarResultadoEnBaseDeDatos(string uuid, string resultado)
        {
            using (var connection = await OpenConnectionAsync())
            {
                var query = @"
                    INSERT INTO ConsultasCFDI (UUID, Resultado, FechaConsulta)
                    VALUES (@UUID, @Resultado, GETDATE())";

                using (var command = new SqlCommand(query, connection))
                {
                    command.Parameters.AddWithValue("@UUID", uuid);
                    command.Parameters.AddWithValue("@Resultado", resultado);
                    await command.ExecuteNonQueryAsync();
                }
            }
        }
    }

    // Clases para el servicio SOAP
    [System.ServiceModel.ServiceContractAttribute(Namespace = "http://tempuri.org/")]
    public interface IConsultaCFDIService
    {
        [System.ServiceModel.OperationContractAttribute(Action = "http://tempuri.org/IConsultaCFDIService/Consulta", ReplyAction = "http://tempuri.org/IConsultaCFDIService/ConsultaResponse")]
        Task<ConsultaResponse> ConsultaAsync(ConsultaRequest request);
    }

    public partial class ConsultaCFDIServiceClient : System.ServiceModel.ClientBase<IConsultaCFDIService>, IConsultaCFDIService
    {
        public ConsultaCFDIServiceClient(System.ServiceModel.Channels.Binding binding, System.ServiceModel.EndpointAddress remoteAddress) :
            base(binding, remoteAddress)
        {
        }

        public Task<ConsultaResponse> ConsultaAsync(ConsultaRequest request)
        {
            return base.Channel.ConsultaAsync(request);
        }
    }

    // Clases para la solicitud y respuesta
    [System.SerializableAttribute()]
    [System.Xml.Serialization.XmlTypeAttribute(Namespace = "http://tempuri.org/")]
    public partial class ConsultaRequest
    {
        public string expresionImpresa { get; set; }

        public ConsultaRequest()
        {
        }

        public ConsultaRequest(string expresionImpresa)
        {
            this.expresionImpresa = expresionImpresa;
        }
    }

    [System.SerializableAttribute()]
    [System.Xml.Serialization.XmlTypeAttribute(Namespace = "http://tempuri.org/")]
    public partial class ConsultaResponse
    {
        public string ConsultaResult { get; set; }
    }
}