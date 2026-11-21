using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using QuicPunch.Helpers;

namespace QuicPunch.Discovery;

public sealed partial class PeerDiscoveryManager
{
    internal sealed record WanAnnouncement(string wan, string? name, string certPubKey, string sig, string presence,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WanRouteAnnouncement? route);
    internal sealed record WanRouteAnnouncement(long revision, string[] endpoints, string sig);
    internal sealed record TorAnnouncement(string tor, string? name, string certPubKey, string sig, string presence);

    private readonly object _wanAnnouncementWriter = new();
    private readonly object _wanAnnouncementReader = new();
    private string? _lastWanAnnouncementContent;
    private string? _lastWanAnnouncementPayload;
    private long _wanAnnouncementRevision;
    private int _wanAnnouncementQueued;
    private int _wanAnnouncementPublishing;

    private static string SignedWanRoute(string token, string author, string presence, long revision, string[] endpoints) =>
        string.Join('\n', new[] { "QuicNet WAN routes v1", token, author.ToLowerInvariant(), presence,
            revision.ToString(CultureInfo.InvariantCulture) }.Concat(endpoints));

    private long NextWanAnnouncementRevision()
    {
        string? path = _context.WanAnnouncementRevisionPath;
        try
        {
            if (_wanAnnouncementRevision == 0 && path != null && File.Exists(path) &&
                long.TryParse(File.ReadAllText(path), NumberStyles.None, CultureInfo.InvariantCulture, out var saved) &&
                saved is > 0 and < long.MaxValue)
                _wanAnnouncementRevision = saved;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            QuicPunchLog.Info("[WAN RECOVERY] Could not read the previous announcement revision.");
        }
        long revision = Math.Max(DateTime.UtcNow.Ticks, _wanAnnouncementRevision + 1);
        if (path != null)
        {
            string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temp, revision.ToString(CultureInfo.InvariantCulture));
                File.Move(temp, path, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                QuicPunchLog.Info("[WAN RECOVERY] Announcement revision kept in memory.");
            }
            finally
            {
                try { File.Delete(temp); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
        return _wanAnnouncementRevision = revision;
    }

    private string SerializeWanAnnouncement(string token, IPEndPoint[] candidates,
        byte[] publicKey, System.Security.Cryptography.X509Certificates.X509Certificate2 cert)
    {
        string author = _context.WanNostrPublicKeyHex ?? "";
        string presence = _wanPresenceNonce;
        string? name = _context.LocalWanPeer?.Name;
        string[] endpoints = candidates.Select(ep => ep.ToString()).ToArray();
        string content = SignedWanRoute(token, author, presence, 0, endpoints) + "\n" + name;
        if (string.Equals(content, _lastWanAnnouncementContent, StringComparison.Ordinal))
            return _lastWanAnnouncementPayload!;

        WanRouteAnnouncement? route = null;
        if (author.Length != 0)
        {
            long revision = NextWanAnnouncementRevision();
            byte[] signature = Utilities.SignToken(SignedWanRoute(token, author, presence, revision, endpoints), cert);
            route = new(revision, endpoints, Convert.ToBase64String(signature));
        }
        string payload = System.Text.Json.JsonSerializer.Serialize(new WanAnnouncement(token, name, Convert.ToBase64String(publicKey),
            Convert.ToBase64String(Utilities.SignToken(token, cert)), presence, route), AnnouncementJsonContext.Default.WanAnnouncement);
        _lastWanAnnouncementContent = content;
        if (route != null)
            QuicPunchLog.Info($"[WAN RECOVERY] Prepared announcement {route.revision} ({route.endpoints.Length} public candidates).");
        return _lastWanAnnouncementPayload = payload;
    }

    private static bool TryApplyWanRoute(PeerInfo peer, string token, string? author, string? presence,
        byte[] certPublicKey, WanRouteAnnouncement route)
    {
        if (route.revision <= 0 || route.endpoints == null || route.endpoints.Length > EndpointAddressCodec.MaxWanCandidates ||
            string.IsNullOrWhiteSpace(author) || string.IsNullOrWhiteSpace(presence) || string.IsNullOrWhiteSpace(route.sig) ||
            route.endpoints.Any(value => value == null || value.Length is < 3 or > 64) ||
            !Utilities.TryCreateECDsaFromPublicKey(certPublicKey, out var key) || key == null)
            return false;
        using (key)
        {
            byte[] signature = Convert.FromBase64String(route.sig);
            byte[] message = Encoding.UTF8.GetBytes(SignedWanRoute(token, author, presence, route.revision, route.endpoints));
            if (!key.VerifyData(message, signature, HashAlgorithmName.SHA256)) return false;
        }

        var candidates = EndpointAddressCodec.ParseWanCandidates(route.endpoints);
        if (!candidates.Select(ep => ep.ToString()).SequenceEqual(route.endpoints)) return false;
        peer.WanCandidates = candidates;
        peer.WanRouteRevision = route.revision;
        return true;
    }

    internal void QueueWanAnnouncement()
    {
        var lifetime = _context.LifecycleToken;
        if (!WanNostrDiscoveryEnabled || lifetime.IsCancellationRequested) return;
        Interlocked.Exchange(ref _wanAnnouncementQueued, 1);
        if (Interlocked.CompareExchange(ref _wanAnnouncementPublishing, 1, 0) == 0)
            _ = Task.Run(() => PublishQueuedWanAnnouncementsAsync(lifetime));
    }

    private async Task PublishQueuedWanAnnouncementsAsync(CancellationToken lifetime)
    {
        try
        {
            while (!lifetime.IsCancellationRequested && Interlocked.Exchange(ref _wanAnnouncementQueued, 0) != 0)
            {
                if (WanNostrDiscovery is { IsRunning: true } discovery)
                {
                    using var send = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
                    send.CancelAfter(TimeSpan.FromSeconds(2));
                    await discovery.PublishNowAsync(send.Token).ConfigureAwait(false);
                }
                await Task.Delay(150, lifetime).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        finally
        {
            Interlocked.Exchange(ref _wanAnnouncementPublishing, 0);
            if (Volatile.Read(ref _wanAnnouncementQueued) != 0)
                QueueWanAnnouncement();
        }
    }

    [JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
    [JsonSerializable(typeof(WanAnnouncement))]
    [JsonSerializable(typeof(WanRouteAnnouncement))]
    [JsonSerializable(typeof(TorAnnouncement))]
    internal partial class AnnouncementJsonContext : JsonSerializerContext { }
}
