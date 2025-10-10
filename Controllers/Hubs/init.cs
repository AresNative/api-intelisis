using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Caching.Memory;

namespace MyApiProject.Hubs
{
    public class GeneralHubs : Hub
    {
        private static readonly HashSet<string> ConnectedGroups = new HashSet<string>();

        public override async Task OnConnectedAsync()
        {
            // Unirse al grupo general de pedidos
            await Groups.AddToGroupAsync(Context.ConnectionId, "PedidosGeneral");
            ConnectedGroups.Add("PedidosGeneral");

            await base.OnConnectedAsync();
        }

        public override async Task OnDisconnectedAsync(Exception? exception)
        {
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, "PedidosGeneral");
            await base.OnDisconnectedAsync(exception);
        }

        // Método para unirse a un pedido específico
        public async Task UnirseAPedido(int pedidoId)
        {
            var groupName = $"Pedido_{pedidoId}";
            await Groups.AddToGroupAsync(Context.ConnectionId, groupName);
            ConnectedGroups.Add(groupName);
        }

        // Método para dejar un pedido específico
        public async Task SalirDePedido(int pedidoId)
        {
            var groupName = $"Pedido_{pedidoId}";
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, groupName);
        }

        private readonly IMemoryCache _memoryCache;

        public GeneralHubs(IMemoryCache memoryCache)
        {
            _memoryCache = memoryCache;
        }

        // Métodos para notificar cambios en las entidades
        public async Task NotifyClientesChanged(string action, object data)
        {
            await Clients.All.SendAsync("ReceiveClientesUpdate", action, data);
        }

        public async Task NotifyCitasChanged(string action, object data)
        {
            await Clients.All.SendAsync("ReceiveCitasUpdate", action, data);
        }

        public async Task NotifyListasChanged(string action, object data)
        {
            await Clients.All.SendAsync("ReceiveListasUpdate", action, data);
        }

        // Métodos para manejo de caché
        public void CacheClientesData(string key, object data)
        {
            _memoryCache.Set(key, data, TimeSpan.FromMinutes(30));
        }

        public object GetCachedClientesData(string key)
        {
            return _memoryCache.TryGetValue(key, out object value) ? value : null;
        }

        // Métodos para grupos (si necesitas notificaciones específicas)
        public async Task AddToGroup(string groupName)
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, groupName);
        }

        public async Task RemoveFromGroup(string groupName)
        {
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, groupName);
        }
    }
}