using System.Net.WebSockets;
using System.Reflection;
using QuicNet.WebUi;

namespace QuicPunchTests.Tests;

internal static class ScreenPreviewTransportTests
{
    public static async Task RunAsync()
    {
        var type = typeof(WebUiWebSocketHub).GetNestedType("ClientConnection", BindingFlags.NonPublic)!;
        foreach (bool stalled in new[] { false, true })
        {
            var socket = new FailedPreviewSocket(stalled);
            var client = Activator.CreateInstance(type, socket)!;
            try
            {
                var send = (Task)type.GetMethod("SendBinaryAsync")!.Invoke(client,
                    new object[] { new ReadOnlyMemory<byte>([1, 2, 3]), CancellationToken.None })!;
                await send.WaitAsync(TimeSpan.FromSeconds(4));
                if (socket.State != WebSocketState.Aborted)
                    throw new Exception("Failed preview must be disconnected without propagating failure to the encoder.");
            }
            finally { await ((IAsyncDisposable)client).DisposeAsync(); }
        }
        Console.WriteLine("[PREVIEW] Closed and stalled WebSocket viewers do not fail or indefinitely block the encoder.");
    }

    private sealed class FailedPreviewSocket(bool stalled) : WebSocket
    {
        private WebSocketState _state = WebSocketState.Open;
        public override WebSocketState State => _state;
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override string? SubProtocol => null;
        public override void Abort() => _state = WebSocketState.Aborted;
        public override void Dispose() { }
        public override Task CloseAsync(WebSocketCloseStatus status, string? description, CancellationToken ct) => Task.CompletedTask;
        public override Task CloseOutputAsync(WebSocketCloseStatus status, string? description, CancellationToken ct) => Task.CompletedTask;
        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken ct) => throw new NotSupportedException();
        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType type, bool end, CancellationToken ct)
            => stalled ? Task.Delay(Timeout.Infinite, ct) : Task.FromException(new WebSocketException("Simulated preview disconnect"));
    }
}
