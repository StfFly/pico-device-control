using Microsoft.AspNetCore.SignalR;
using System.Threading.Tasks;

namespace PicoManager.Server.Hubs
{
    public class PicoHub : Hub
    {
        public async Task JoinRoom(string roomName)
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, roomName);
        }

        public async Task LeaveRoom(string roomName)
        {
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, roomName);
        }
    }
}
