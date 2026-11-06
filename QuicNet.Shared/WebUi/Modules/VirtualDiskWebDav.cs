using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using System.Runtime.InteropServices;
using QuicNet.VirtualDisk;

namespace QuicNet.WebUi.Modules;

/// <summary>Loopback-only WebDAV class 1 bridge. Each unpredictable capability addresses exactly one remote share.</summary>
internal sealed class VirtualDiskWebDav(VirtualDiskApiModule api)
{
    private sealed record Mount(Guid Peer, Guid Share);
    private readonly ConcurrentDictionary<string, Mount> _mounts = new();
    private readonly ConcurrentDictionary<string, string> _mappedPaths = new();
    private static readonly XNamespace Dav = "DAV:";
    private const string Prefix = "/virtual-disk-dav/";

    internal async Task<DiskReply> CreateAsync(Guid peer, Guid share, int port)
    {
        var allowed = await api.ExecuteAsync(peer, new("shares"));
        if (!allowed.Success || allowed.Shares?.Any(s => s.Id == share) != true) return new(false, "Carpeta no autorizada.", "Denied");
        string? existing = _mounts.FirstOrDefault(item => item.Value.Peer == peer && item.Value.Share == share).Key;
        if (existing == null && _mounts.Count >= 32) return new(false, "Desconecta una unidad antes de crear otra.");
        string token = existing ?? Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        _mounts[token] = new(peer, share);
        string url = $"http://127.0.0.1:{port}{Prefix}{token}/";
        string unc = $"\\\\127.0.0.1@{port}\\DavWWWRoot\\virtual-disk-dav\\{token}";
        return new(true, MountUrl: url, NativePath: OperatingSystem.IsWindows() ? unc : url);
    }

    internal void MarkMapped(string url, string nativePath) => _mappedPaths[new Uri(url).AbsolutePath.TrimEnd('/').Split('/').Last()] = nativePath;
    internal bool IsMapped(string url) => _mappedPaths.ContainsKey(new Uri(url).AbsolutePath.TrimEnd('/').Split('/').Last());

    internal async Task RemoveAsync(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return;
        string token = uri.AbsolutePath.TrimEnd('/').Split('/').Last();
        _mounts.TryRemove(token, out _);
        if (_mappedPaths.TryRemove(token, out string? nativePath) && FindWindowsMapping(nativePath) is { } drive) await RunNetAsync(drive, "/delete", "/y");
    }

    internal void Clear()
    {
        _mounts.Clear();
        foreach (string token in _mappedPaths.Keys)
            if (_mappedPaths.TryRemove(token, out string? nativePath) && FindWindowsMapping(nativePath) is { } drive) _ = RunNetAsync(drive, "/delete", "/y");
    }

    public async Task<bool> HandleAsync(HttpListenerRequest req, HttpListenerResponse resp)
    {
        string absolute = req.Url?.AbsolutePath ?? "";
        if (!absolute.StartsWith(Prefix, StringComparison.Ordinal)) return false;
        string rest = absolute[Prefix.Length..];
        int slash = rest.IndexOf('/');
        string token = slash < 0 ? rest : rest[..slash];
        if (!_mounts.TryGetValue(token, out var mount)) { resp.StatusCode = 404; return true; }
        var shares = await api.ExecuteAsync(mount.Peer, new("shares"));
        var share = shares.Shares?.FirstOrDefault(s => s.Id == mount.Share);
        if (!shares.Success || share == null) { _mounts.TryRemove(token, out _); resp.StatusCode = 403; return true; }
        string relative = slash < 0 ? "" : Uri.UnescapeDataString(rest[(slash + 1)..]).TrimEnd('/');
        // Never render file content as HTML in the app origin.
        resp.ContentType = "application/octet-stream";
        resp.Headers["Content-Disposition"] = "attachment";
        resp.Headers["DAV"] = "1";
        resp.Headers["Allow"] = share.Writable ? "OPTIONS, PROPFIND, GET, HEAD, PUT, MKCOL, DELETE, LOCK, UNLOCK" : "OPTIONS, PROPFIND, GET, HEAD";
        string lockText = req.Headers["If"] ?? req.Headers["Lock-Token"] ?? "";
        Guid leaseId = Guid.Empty;
        const string lockPrefix = "opaquelocktoken:";
        int lockStart = lockText.IndexOf(lockPrefix, StringComparison.Ordinal);
        if (lockStart >= 0 && lockText.Length >= lockStart + lockPrefix.Length + 36) Guid.TryParse(lockText.Substring(lockStart + lockPrefix.Length, 36), out leaseId);
        switch (req.HttpMethod)
        {
            case "OPTIONS": resp.StatusCode = 200; break;
            case "PROPFIND":
            {
                if (req.Headers["Depth"] is not ("0" or "1")) { resp.StatusCode = 403; break; }
                var listing = await api.ExecuteAsync(mount.Peer, new("list", ShareId: mount.Share, Path: relative));
                var items = new List<DiskEntry>();
                if (listing.Success)
                {
                    items.Add(new(share.Name, relative, true));
                    if (req.Headers["Depth"] == "1")
                    {
                        items.AddRange(listing.Entries ?? []);
                        // Never truncate an OS directory listing silently.
                        int skip = listing.Entries?.Length ?? 0;
                        while (listing.More)
                        {
                            listing = await api.ExecuteAsync(mount.Peer, new("list", ShareId: mount.Share, Path: relative, Skip: skip));
                            if (!listing.Success) break;
                            items.AddRange(listing.Entries ?? []); skip += listing.Entries?.Length ?? 0;
                            if (skip > 10_000) { resp.StatusCode = 507; return true; }
                        }
                        if (!listing.Success) { resp.StatusCode = 403; break; }
                    }
                }
                else
                {
                    var file = await api.ExecuteAsync(mount.Peer, new("open-read", ShareId: mount.Share, Path: relative));
                    if (!file.Success) { resp.StatusCode = 404; break; }
                    await api.ExecuteAsync(mount.Peer, new("close", TransferId: file.TransferId));
                    items.Add(new(System.IO.Path.GetFileName(relative), relative, false, file.Length));
                }
                string basePath = Prefix + token + "/";
                var document = new XDocument(new XElement(Dav + "multistatus", items.Select(item =>
                    new XElement(Dav + "response", new XElement(Dav + "href", basePath + string.Join('/', item.Path.Split('/').Select(Uri.EscapeDataString)) + (item.Directory && item.Path.Length > 0 ? "/" : "")),
                        new XElement(Dav + "propstat", new XElement(Dav + "prop",
                            new XElement(Dav + "displayname", item.Name),
                            new XElement(Dav + "resourcetype", item.Directory ? new XElement(Dav + "collection") : null),
                            new XElement(Dav + "getcontentlength", item.Length),
                            new XElement(Dav + "getlastmodified", (item.ModifiedUtc ?? DateTime.UtcNow).ToString("R")),
                            new XElement(Dav + "supportedlock", new XElement(Dav + "lockentry", new XElement(Dav + "lockscope", new XElement(Dav + "exclusive")), new XElement(Dav + "locktype", new XElement(Dav + "write"))))),
                            new XElement(Dav + "status", "HTTP/1.1 200 OK"))))));
                byte[] body = Encoding.UTF8.GetBytes(document.ToString());
                resp.StatusCode = 207; resp.ContentType = "application/xml; charset=utf-8"; resp.ContentLength64 = body.Length;
                await resp.OutputStream.WriteAsync(body); break;
            }
            case "HEAD":
            case "GET":
            {
                var read = await api.ExecuteAsync(mount.Peer, new("open-read", ShareId: mount.Share, Path: relative));
                if (!read.Success) { resp.StatusCode = 404; break; }
                try
                {
                    resp.Headers["ETag"] = '"' + read.Version + '"'; resp.ContentLength64 = read.Length;
                    if (req.HttpMethod == "HEAD") break;
                    for (long offset = 0; offset < read.Length;)
                    {
                        var part = await api.ExecuteAsync(mount.Peer, new("read", TransferId: read.TransferId, Offset: offset));
                        if (!part.Success || part.Data is not { Length: > 0 }) { resp.Abort(); break; }
                        await resp.OutputStream.WriteAsync(part.Data); offset += part.Data.Length;
                    }
                }
                finally { await api.ExecuteAsync(mount.Peer, new("close", TransferId: read.TransferId)); }
                break;
            }
            case "PUT":
            {
                if (!share.Writable) { resp.StatusCode = 403; break; }
                if (req.ContentLength64 < 0 || req.ContentLength64 > VirtualDiskService.MaxFileBytes) { resp.StatusCode = 413; break; }
                // Overwrites require a content version. Class 1 clients that omit it fail safely.
                string? version = req.Headers["If-Match"]?.Trim('"');
                var write = await api.ExecuteAsync(mount.Peer, new("begin-write", ShareId: mount.Share, Path: relative, Length: req.ContentLength64, Version: version, LeaseId: leaseId));
                if (!write.Success) { resp.StatusCode = write.Code == "Conflict" ? 412 : write.Code == "Locked" ? 423 : 403; break; }
                try
                {
                    byte[] buffer = new byte[VirtualDiskService.ChunkBytes]; long offset = 0;
                    while (offset < req.ContentLength64)
                    {
                        int count = (int)Math.Min(buffer.Length, req.ContentLength64 - offset);
                        await req.InputStream.ReadExactlyAsync(buffer.AsMemory(0, count));
                        var result = await api.ExecuteAsync(mount.Peer, new("write", TransferId: write.TransferId, Offset: offset, Data: buffer[..count]));
                        if (!result.Success) { resp.StatusCode = 403; return true; }
                        offset += count;
                    }
                    var commit = await api.ExecuteAsync(mount.Peer, new("commit", TransferId: write.TransferId));
                    resp.StatusCode = commit.Success ? version == null ? 201 : 204 : commit.Code == "Conflict" ? 412 : 403;
                    if (commit.Success) resp.Headers["ETag"] = '"' + commit.Version + '"';
                }
                finally { await api.ExecuteAsync(mount.Peer, new("close", TransferId: write.TransferId)); }
                break;
            }
            case "MKCOL":
            case "DELETE":
            {
                var result = await api.ExecuteAsync(mount.Peer, new(req.HttpMethod == "MKCOL" ? "mkdir" : "delete", ShareId: mount.Share, Path: relative, Version: req.Headers["If-Match"]?.Trim('"'), LeaseId: leaseId));
                resp.StatusCode = result.Success ? req.HttpMethod == "MKCOL" ? 201 : 204 : result.Code == "Conflict" ? 412 : 403;
                break;
            }
            case "LOCK":
            case "UNLOCK":
            {
                if (req.Headers["Depth"] is not (null or "0" or "infinity")) { resp.StatusCode = 400; break; }
                var result = await api.ExecuteAsync(mount.Peer, new(req.HttpMethod == "LOCK" ? "lock" : "unlock", ShareId: mount.Share, Path: relative, LeaseId: leaseId));
                if (!result.Success) { resp.StatusCode = result.Code == "Locked" ? 423 : 403; break; }
                if (req.HttpMethod == "UNLOCK") { resp.StatusCode = 204; break; }
                string lockToken = "opaquelocktoken:" + result.LeaseId;
                resp.Headers["Lock-Token"] = "<" + lockToken + ">";
                var xml = new XElement(Dav + "prop", new XElement(Dav + "lockdiscovery", new XElement(Dav + "activelock",
                    new XElement(Dav + "lockscope", new XElement(Dav + "exclusive")), new XElement(Dav + "locktype", new XElement(Dav + "write")),
                    new XElement(Dav + "depth", "0"), new XElement(Dav + "timeout", "Second-120"), new XElement(Dav + "locktoken", new XElement(Dav + "href", lockToken)))));
                byte[] body = Encoding.UTF8.GetBytes(xml.ToString());
                resp.ContentType = "application/xml; charset=utf-8"; resp.ContentLength64 = body.Length; await resp.OutputStream.WriteAsync(body);
                break;
            }
            default: resp.StatusCode = 405; break;
        }
        WebUiServer.LogEvent($"[DISK DAV] {req.HttpMethod}: {resp.StatusCode}");
        return true;
    }

    internal static Task<bool> MapWindowsDriveAsync(string nativePath) => RunNetAsync("*", nativePath, "/persistent:no");

    [DllImport("mpr.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int WNetGetConnectionW(string localName, StringBuilder remoteName, ref int length);

    internal static string? FindWindowsMapping(string nativePath)
    {
        if (!OperatingSystem.IsWindows()) return null;
        static string Canonical(string value) => value.Replace("\\DavWWWRoot", "", StringComparison.OrdinalIgnoreCase).TrimEnd('\\');
        string expected = Canonical(nativePath);
        // Query the mapping table, never probe volume readiness (an unrelated offline drive can block that).
        for (char letter = 'A'; letter <= 'Z'; letter++)
        {
            int length = 2048; var remote = new StringBuilder(length);
            if (WNetGetConnectionW(letter + ":", remote, ref length) == 0 && Canonical(remote.ToString()).Equals(expected, StringComparison.OrdinalIgnoreCase)) return letter + ":";
        }
        return null;
    }

    private static async Task<bool> RunNetAsync(params string[] args)
    {
        if (!OperatingSystem.IsWindows()) return false;
        var info = new ProcessStartInfo(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "net.exe"))
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        info.ArgumentList.Add("use"); foreach (string arg in args) info.ArgumentList.Add(arg);
        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try { await process.WaitForExitAsync(deadline.Token); }
        catch (OperationCanceledException) { try { process.Kill(); } catch { } return false; }
        await Task.WhenAll(output, error);
        return process.ExitCode == 0;
    }
}
