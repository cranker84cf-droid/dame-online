using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using CheckersOnline;
using CheckersOnline.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<PlayerStore>();
builder.Services.AddSingleton<RoomManager>();

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();
app.UseWebSockets();

app.MapGet("/health", () => Results.Ok(new { ok = true }));

app.Map("/ws", async context =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = 400;
        return;
    }

    var manager = context.RequestServices.GetRequiredService<RoomManager>();
    var socket = await context.WebSockets.AcceptWebSocketAsync();
    var connectionId = Guid.NewGuid().ToString("N");
    await manager.RegisterSocketAsync(connectionId, socket);

    var buffer = new byte[16 * 1024];
    try
    {
        while (socket.State == WebSocketState.Open)
        {
            var result = await socket.ReceiveAsync(buffer, context.RequestAborted);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                break;
            }

            var text = Encoding.UTF8.GetString(buffer, 0, result.Count);
            var envelope = JsonSerializer.Deserialize<ClientEnvelope>(text, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            if (envelope is not null)
            {
                await manager.HandleMessageAsync(connectionId, envelope);
            }
        }
    }
    finally
    {
        await manager.DisconnectAsync(connectionId);
        if (socket.State != WebSocketState.Closed)
        {
            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "closing", CancellationToken.None);
        }
    }
});

app.Run();
