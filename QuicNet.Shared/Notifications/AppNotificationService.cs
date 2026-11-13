using System.Collections.Concurrent;
using System.Threading.Channels;
using QuicNet.Settings;

namespace QuicNet.Notifications;

public sealed record AppNotice(string Id, string Category, string Title, string Message, string Url,
    bool Popup, bool Sound, int Volume, string Tone);
internal sealed record NotificationTestResult(bool success, bool sent, string? reason, string message);
public sealed record NotificationPlatformStatus(bool Enabled, bool SoundEnabled, bool CanOpenSettings, bool SystemControlsSound, string Message);

public interface IAppPlatform
{
    bool StartupSupported { get; }
    string Description { get; }
    void SetStartWithSystem(bool enabled);
    Task NotifyAsync(AppNotice notice, bool desktop, CancellationToken ct);
    Task PlayRingtoneAsync(int volume, CancellationToken ct);
    TimeSpan? GetUserIdleTime() => null;
    NotificationPlatformStatus? GetNotificationStatus(string category = "system") => null;
    bool OpenNotificationSettings() => false;
    void DismissNotification(string id) { }
}

internal sealed class AppNotificationService : IDisposable
{
    private readonly AppPreferencesStore _preferences;
    private readonly Action<AppNotice> _broadcast;
    private readonly Channel<(AppNotice Notice, string? Hash, DateTimeOffset Created)> _queue = Channel.CreateBounded<(AppNotice, string?, DateTimeOffset)>(new BoundedChannelOptions(64) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _worker;
    private readonly Task _ringer;
    private readonly ConcurrentDictionary<Guid, string> _ringingCalls = new();
    private readonly SemaphoreSlim _ringChanged = new(0, 1);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _seen = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _focused = new();
    public IAppPlatform? Platform { get; set; }
    public AppNotificationService(AppPreferencesStore preferences, Action<AppNotice> broadcast)
    {
        _preferences = preferences; _broadcast = broadcast;
        _preferences.Changed += RingChanged;
        _worker = RunAsync(); _ringer = RingAsync();
    }

    private void RingChanged()
    {
        try { _ringChanged.Release(); } catch (SemaphoreFullException) { }
    }

    internal void RingIncomingCall(Guid request, string hash, Task decision)
    {
        _ringingCalls[request] = hash;
        RingChanged();
        _ = decision.ContinueWith(completed => {
            _ringingCalls.TryRemove(request, out _); RingChanged();
            try { Platform?.DismissNotification(request.ToString("N")); }
            catch (Exception ex) { WebUi.WebUiServer.LogEvent("[NOTIFICATIONS] " + ex.Message); }
        },
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private async Task RingAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                if (_ringingCalls.IsEmpty) { await _ringChanged.WaitAsync(_stop.Token); continue; }
                _ringChanged.Wait(0);
                using var cycle = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                var changed = _ringChanged.WaitAsync(cycle.Token);
                var sound = RingCycleAsync(cycle.Token);
                await Task.WhenAny(changed, sound);
                cycle.Cancel();
                try { await Task.WhenAll(changed, sound); }
                catch (OperationCanceledException) { }
                catch (Exception ex) { WebUi.WebUiServer.LogEvent("[RINGTONE] " + ex.Message); await Task.Delay(1000, _stop.Token); }
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task RingCycleAsync(CancellationToken ct)
    {
        var settings = _preferences.Snapshot().Notifications;
        bool focused = _focused.Values.Any(time => DateTimeOffset.UtcNow - time < TimeSpan.FromSeconds(35));
        var notice = _ringingCalls.Values.Select(hash => Decide(settings, "calls", "", "", "/call.html", hash,
            focused, DateTimeOffset.Now, "")).FirstOrDefault(value => value?.Sound == true);
        if (notice != null && Platform is { } platform)
        {
            await platform.PlayRingtoneAsync(notice.Volume, ct);
            await Task.Delay(2000, ct);
        }
        else await Task.Delay(500, ct);
    }

    internal void Presence(string client, bool focused)
    {
        if (client.Length > 64) return;
        if (focused) _focused[client] = DateTimeOffset.UtcNow; else _focused.TryRemove(client, out _);
        RingChanged();
    }
    internal bool AllowsInApp(string category, string? certHash)
    {
        var settings = _preferences.Snapshot().Notifications;
        bool focused = _focused.Values.Any(time => DateTimeOffset.UtcNow - time < TimeSpan.FromSeconds(35));
        return settings.InApp && Decide(settings, category, "", "", "/", certHash, focused, DateTimeOffset.Now, "")?.Popup == true;
    }

    internal NotificationTestResult TestNotification()
    {
        var settings = _preferences.Snapshot().Notifications;
        var now = DateTimeOffset.Now;
        bool focused = _focused.Values.Any(time => DateTimeOffset.UtcNow - time < TimeSpan.FromSeconds(35));
        NotificationTestResult Blocked(string reason, string message) => new(true, false, reason, message);
        if (settings.DoNotDisturb && (settings.DoNotDisturbUntil == null || now < settings.DoNotDisturbUntil))
            return Blocked("do_not_disturb", "Estás en modo No molestar. Los avisos y las llamadas están silenciados. Desactívalo para probar una notificación.");
        if (!settings.Enabled) return Blocked("disabled", "Las notificaciones están desactivadas. Actívalas para enviar la prueba.");
        if (settings.IsQuiet(now)) return Blocked("quiet_hours", "Estás dentro del horario silencioso. Desactívalo para probar una notificación.");
        if (focused && !settings.WhileFocused) return Blocked("focused", "Los avisos están silenciados mientras usas QuicNet. Activa «Avisar mientras uso la aplicación» para probarlos aquí.");
        if (Platform?.GetNotificationStatus() is { Enabled: false } blocked && (settings.Desktop || settings.Sound))
            return Blocked("system_blocked", blocked.Message);
        var notice = Decide(settings, "system", "QuicNet", "Así recibirás tus notificaciones.", "/settings.html", null, focused, now, "test");
        if (notice == null) return Blocked("feature_disabled", "Los avisos de la aplicación están desactivados en «Por función».");
        if (!(settings.InApp && notice.Popup) && !(Platform != null && ((settings.Desktop && notice.Popup) || notice.Sound)))
            return Blocked("no_delivery", "La prueba no tiene ninguna salida activa. Revisa las ventanas, el sonido y el volumen de las notificaciones.");
        Publish("system", "QuicNet", "Así recibirás tus notificaciones.", "/settings.html");
        return new(true, true, null, "Prueba enviada.");
    }
    internal static AppNotice? Decide(NotificationPreferences settings, string category, string title,
        string message, string url, string? certHash, bool focused, DateTimeOffset now, string id)
    {
        if (!NotificationPreferences.Categories.Contains(category) || settings.IsQuiet(now, focused)) return null;
        var feature = settings.Features.GetValueOrDefault(category) ?? new();
        var contact = certHash == null ? new NotificationRule() :
            settings.Contacts.GetValueOrDefault(certHash.ToUpperInvariant())?.Resolve(settings.ContactDefaults) ?? settings.ContactDefaults;
        if (!feature.Enabled || !contact.Enabled) return null;
        return new(id, category, settings.Preview ? title : "QuicNet", settings.Preview ? message : "Tienes una nueva notificación.", url,
            feature.Popup && contact.Popup, settings.Sound && feature.Sound && contact.Sound && settings.Volume > 0, settings.Volume, settings.Tone);
    }
    public void Publish(string category, string title, string message, string url = "/", string? certHash = null, string? id = null, bool playSound = true)
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var old in _seen.Where(p => now - p.Value > TimeSpan.FromMinutes(2))) _seen.TryRemove(old.Key, out _);
        foreach (var old in _focused.Where(p => now - p.Value > TimeSpan.FromSeconds(35))) _focused.TryRemove(old.Key, out _);
        if (id != null && !_seen.TryAdd(id, now)) return;
        var settings = _preferences.Snapshot().Notifications;
        var notice = Decide(settings, category, title, message, url, certHash, !_focused.IsEmpty, now.ToLocalTime(), id ?? Guid.NewGuid().ToString("N"));
        if (notice == null) return;
        if (!playSound) notice = notice with { Sound = false };
        if (settings.InApp && notice.Popup) _broadcast(notice);
        if ((settings.Desktop && notice.Popup) || notice.Sound) _queue.Writer.TryWrite((notice, certHash, now));
    }
    private async Task RunAsync()
    {
        try
        {
            await foreach (var item in _queue.Reader.ReadAllAsync(_stop.Token))
                try
                {
                    if (DateTimeOffset.UtcNow - item.Created > TimeSpan.FromSeconds(10)) continue;
                    var current = _preferences.Snapshot().Notifications;
                    bool focused = _focused.Values.Any(time => DateTimeOffset.UtcNow - time < TimeSpan.FromSeconds(35));
                    var notice = Decide(current, item.Notice.Category, item.Notice.Title, item.Notice.Message, item.Notice.Url,
                        item.Hash, focused, DateTimeOffset.Now, item.Notice.Id);
                    if (notice != null && Platform is { } platform) await platform.NotifyAsync(notice with { Sound = notice.Sound && item.Notice.Sound }, current.Desktop, _stop.Token);
                }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
                catch (Exception ex) { WebUi.WebUiServer.LogEvent("[NOTIFICATIONS] " + ex.Message); }
        }
        catch (OperationCanceledException) { }
    }
    public void Dispose() { _preferences.Changed -= RingChanged; _queue.Writer.TryComplete(); _stop.Cancel(); }
}
