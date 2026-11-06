namespace QuicNet.Watch.Services;

public interface IWatchFilePicker
{
    Task<string?> PickVideoFileAsync();
}
