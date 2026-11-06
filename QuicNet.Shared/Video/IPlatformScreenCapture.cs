namespace QuicNet.Video;

// Capture owns the system permission and resources; the call owns transport and consent per peer.
internal interface IPlatformScreenCapture
{
    Task<NativeScreenProfile> StartAsync(ScreenEncoderOptions options, Func<byte[], Task> output, Action<string?> stopped);
    void Resume();
    Task StopAsync();
}
