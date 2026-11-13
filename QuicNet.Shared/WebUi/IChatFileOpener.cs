namespace QuicNet.WebUi;

/// <summary>Opens an existing attachment resolved and authorized by the chat backend.</summary>
public interface IChatFileOpener
{
    Task OpenAsync(string path, string name, string mime, CancellationToken cancellationToken);
}
