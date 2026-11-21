namespace QuicNet.Video;

internal readonly record struct RemoteInputArea(int X, int Y, int Width, int Height);
internal sealed record RemoteInputMonitor(string name, double x, double y, double width, double height);

internal interface IRemoteInputDevice : IDisposable
{
    void Apply(RemoteInputArea area, RemoteDesktopInput input);
    void Release();
    bool StopRequested => false;
    bool KeyboardAvailable => true;
}

internal interface IPlatformRemoteInput
{
    bool RemoteInputEnabled => true;
    RemoteInputArea? RemoteInputArea { get; }
    IRemoteInputDevice? CreateRemoteInputDevice();
    bool OpenRemoteInputSettings() => false;
}

internal interface IRemoteSessionClipboard
{
    Task<string?> ReadAsync();
    Task<bool> WriteAsync(string text, Func<bool> permitted);
}
