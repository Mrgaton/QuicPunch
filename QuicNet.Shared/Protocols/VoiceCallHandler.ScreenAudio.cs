using QuicNet.Video;
using QuicPunch;

namespace QuicNet.Protocols;

internal sealed partial class VoiceCallHandler
{
    private readonly SemaphoreSlim _screenAudioGate = new(1, 1);
    private WindowsSystemAudioCapture? _systemAudioCapture;
    internal volatile string? SystemAudioShareId;
    internal event Action<string, string>? SystemAudioCaptureFailed;

    internal async Task StartSystemAudioAsync(string shareId)
    {
        await _screenAudioGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!IsLocalScreenSharing || ActiveCalls.IsEmpty || LocalVideoKind != "screen" || SystemAudioShareId != shareId)
                throw new InvalidOperationException("La pantalla que autorizó el audio ya no se está compartiendo.");
            if (_systemAudioCapture != null) return;
            var capture = await WindowsSystemAudioCapture.StartAsync(packet =>
                SystemAudioShareId == shareId ? SendScreenAudioAsync(packet) : Task.CompletedTask).ConfigureAwait(false);
            if (!IsLocalScreenSharing || ActiveCalls.IsEmpty || SystemAudioShareId != shareId)
            {
                await capture.DisposeAsync().ConfigureAwait(false);
                throw new InvalidOperationException("La pantalla se dejó de compartir durante el inicio del audio.");
            }
            capture.Failed += error =>
            {
                QuicPunchLog.Error("[SCREEN AUDIO] La captura sin audio de llamada se detuvo: " + error.Message);
                SystemAudioCaptureFailed?.Invoke(shareId, "Se ha detenido el audio compartido: " + error.Message);
            };
            _systemAudioCapture = capture;
        }
        finally { _screenAudioGate.Release(); }
    }

    internal async Task StopSystemAudioAsync(string? shareId = null)
    {
        if (shareId == null) SystemAudioShareId = null;
        await _screenAudioGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (shareId != null && SystemAudioShareId != shareId) return;
            SystemAudioShareId = null;
            if (_systemAudioCapture is { } capture)
            {
                _systemAudioCapture = null;
                await capture.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally { _screenAudioGate.Release(); }
    }
}
