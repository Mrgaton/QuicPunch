namespace QuicNet.Watch.Services;

public interface IWatchFilePicker
{
    Task<string?> PickVideoFileAsync();
    // Called only after the room has accepted the new source. Mobile pickers can
    // now retire their previous imported copy without breaking active playback.
    void MediaSelected(string path) { }
}
