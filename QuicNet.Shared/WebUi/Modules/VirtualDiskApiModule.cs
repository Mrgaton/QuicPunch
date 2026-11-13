using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using QuicNet.VirtualDisk;

namespace QuicNet.WebUi.Modules;

internal sealed class VirtualDiskApiModule : IDisposable
{
    private readonly QuicPunch.QuicPunch _qcc;
    internal readonly VirtualDiskService Service;
    internal readonly VirtualDiskHandler Handler;
    private readonly CancellationToken _lifetime;
    private readonly ConcurrentDictionary<Guid, byte> _connecting = new();
    private readonly VirtualDiskWebDav _dav;

    internal VirtualDiskApiModule(QuicPunch.QuicPunch qcc, CancellationToken lifetime)
    {
        _qcc = qcc; _lifetime = lifetime;
        Service = new(qcc.NodeAppDataPath, hash => {
            try { return qcc.IsTrustedPeer(Convert.FromBase64String(hash)); } catch (FormatException) { return false; }
        }, hash => {
            try { return qcc.PeerStore?.IsAdministrator(Convert.FromBase64String(hash)) == true; } catch (FormatException) { return false; }
        });
        Handler = new(Service);
        qcc.RegisterProtocol(Handler);
        _dav = new(this);
    }

    public async Task<bool> HandleAsync(string path, HttpListenerRequest req, HttpListenerResponse resp)
    {
        if (await _dav.HandleAsync(req, resp)) return true;
        if (!path.StartsWith("/api/virtual-disk/", StringComparison.Ordinal)) return false;
        if (path == "/api/virtual-disk/status" && req.HttpMethod == "GET")
        {
            await WriteAsync(resp, Service.Execute(null, new("shares"))); return true;
        }
        if (path == "/api/virtual-disk/download" && req.HttpMethod == "GET")
        {
            if (!Guid.TryParse(req.QueryString["peerId"], out var peer) || !Guid.TryParse(req.QueryString["shareId"], out var share)) throw new InvalidDataException("Disco inválido.");
            var open = await ExecuteAsync(peer, new("open-read", ShareId: share, Path: req.QueryString["path"] ?? ""));
            if (!open.Success) { await WriteAsync(resp, open, 403); return true; }
            try
            {
                resp.ContentType = "application/octet-stream";
                resp.Headers["Content-Disposition"] = "attachment; filename*=UTF-8''" + Uri.EscapeDataString(System.IO.Path.GetFileName(req.QueryString["path"] ?? "archivo"));
                resp.ContentLength64 = open.Length;
                for (long offset = 0; offset < open.Length;)
                {
                    var chunk = await ExecuteAsync(peer, new("read", TransferId: open.TransferId, Offset: offset));
                    if (!chunk.Success || chunk.Data is not { Length: > 0 }) throw new IOException("Lectura interrumpida o permiso revocado.");
                    await resp.OutputStream.WriteAsync(chunk.Data, _lifetime).ConfigureAwait(false);
                    offset += chunk.Data.Length;
                }
            }
            catch { resp.Abort(); }
            finally { try { await ExecuteAsync(peer, new("close", TransferId: open.TransferId)); } catch { } }
            return true;
        }
        if (req.HttpMethod != "POST") { resp.StatusCode = 405; return true; }
        using var doc = await WebUiContext.ReadJsonAsync(req, path);
        var root = doc.RootElement;
        if (path == "/api/virtual-disk/pick-folder")
        {
            if (Service.Documents == null) { await WriteAsync(resp, new(false, "Selector de Android no disponible.")); return true; }
            string? folder = await Service.Documents.PickFolderAsync(_lifetime);
            await WriteAsync(resp, new(folder != null, folder == null ? "Selección cancelada." : null, NativePath: folder));
            return true;
        }
        Guid target = Guid.Empty;
        if (root.TryGetProperty("peerId", out var id) && id.ValueKind != JsonValueKind.Null && !string.IsNullOrEmpty(id.GetString()) && !Guid.TryParse(id.GetString(), out target))
            throw new InvalidDataException("Identidad de equipo inválida.");
        if (path == "/api/virtual-disk/save")
        {
            if (Service.Documents == null) { await WriteAsync(resp, new(false, "Selector de Android no disponible.")); return true; }
            Guid share = WebUiContext.ParseGuid(root, "shareId");
            string filePath = root.GetProperty("path").GetString() ?? "";
            // Ask for the destination before opening a remote transfer (the picker can stay open indefinitely).
            using var download = await Service.Documents.CreateDownloadAsync(System.IO.Path.GetFileName(filePath), _lifetime);
            if (download == null) { await WriteAsync(resp, new(false, "Guardado cancelado.", "Cancelled")); return true; }
            var output = download.Stream;
            var opened = await ExecuteAsync(target, new("open-read", ShareId: share, Path: filePath));
            if (!opened.Success) { await WriteAsync(resp, opened); return true; }
            try
            {
                using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
                for (long offset = 0; offset < opened.Length;)
                {
                    var chunk = await ExecuteAsync(target, new("read", TransferId: opened.TransferId, Offset: offset));
                    if (!chunk.Success || chunk.Data is not { Length: > 0 } || chunk.Data.Length > opened.Length - offset) throw new IOException("Descarga interrumpida.");
                    await output.WriteAsync(chunk.Data, _lifetime);
                    hash.AppendData(chunk.Data); offset += chunk.Data.Length;
                }
                if (!Convert.ToHexString(hash.GetHashAndReset()).Equals(opened.Version, StringComparison.OrdinalIgnoreCase)) throw new IOException("La comprobación de integridad ha fallado.");
                await output.FlushAsync(_lifetime);
                download.Complete();
                await WriteAsync(resp, new(true));
            }
            finally { try { await ExecuteAsync(target, new("close", TransferId: opened.TransferId)); } catch { } }
            return true;
        }
        if (path == "/api/virtual-disk/mount")
        {
            Guid share = WebUiContext.ParseGuid(root, "shareId");
            var result = await _dav.CreateAsync(target, share, req.Url!.Port);
            if (result.Success && root.TryGetProperty("native", out var native) && native.GetBoolean())
            {
                bool mounted = _dav.IsMapped(result.MountUrl!) || await VirtualDiskWebDav.MapWindowsDriveAsync(result.NativePath!);
                if (mounted) _dav.MarkMapped(result.MountUrl!, result.NativePath!);
                result = result with { Code = mounted ? "Mounted" : "ManualMount" };
            }
            await WriteAsync(resp, result); return true;
        }
        if (path == "/api/virtual-disk/unmount")
        {
            await _dav.RemoveAsync(root.GetProperty("url").GetString() ?? "");
            await WriteAsync(resp, new(true)); return true;
        }
        if (path == "/api/virtual-disk/connect")
        {
            if (!_qcc.AvailablePeers.TryGetValue(target, out var peer) || !_qcc.IsTrustedPeer(peer)) { await WriteAsync(resp, new(false, "Contacto no disponible o sin autorización."), 403); return true; }
            if (!Handler.ConnectedPeers.Contains(target) && _connecting.TryAdd(target, 0))
                _ = ConnectAsync(peer);
            await WriteAsync(resp, new(true, Code: Handler.ConnectedPeers.Contains(target) ? "Connected" : "Connecting")); return true;
        }
        if (path != "/api/virtual-disk/operation") { resp.StatusCode = 404; return true; }
        var request = JsonSerializer.Deserialize(root.GetProperty("request").GetRawText(), DiskJsonContext.Default.DiskRequest)
            ?? throw new InvalidDataException("Operación inválida.");
        try { await WriteAsync(resp, await ExecuteAsync(target, request)); }
        catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException)
        { await WriteAsync(resp, new(false, "El dispositivo no responde. Conecta de nuevo o espera su aceptación.", "Disconnected"), 409); }
        return true;
    }

    internal Task<DiskReply> ExecuteAsync(Guid peer, DiskRequest request) => peer == Guid.Empty
        ? Task.Run(() => Service.Execute(null, request), _lifetime)
        : Handler.RequestAsync(peer, request, _lifetime);

    private async Task ConnectAsync(QuicPunch.PeerInfo peer)
    {
        try { await _qcc.InitQuicConnection(Handler.ProtocolId, peer, cancellationToken: _lifetime).ConfigureAwait(false); }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex) { WebUiServer.LogEvent("[DISK] " + ex.Message); }
        finally { _connecting.TryRemove(peer.Id, out _); }
    }
    private static async Task WriteAsync(HttpListenerResponse resp, DiskReply reply, int status = 200)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(reply, DiskJsonContext.Default.DiskReply);
        resp.StatusCode = status; resp.ContentType = "application/json; charset=utf-8"; resp.ContentLength64 = bytes.Length;
        await resp.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
    }
    public void Dispose() { _dav.Clear(); Handler.Dispose(); Service.Dispose(); }
}
