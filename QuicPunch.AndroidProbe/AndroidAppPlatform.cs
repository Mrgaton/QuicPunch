using Android.App;
using Android.Content;
using Android.Media;
using QuicNet.Notifications;
using QuicNet.Settings;

namespace QuicPunch.AndroidProbe;

internal sealed class AndroidAppPlatform(Context context) : IAppPlatform
{
    private readonly object _noticeGate = new();
    private readonly Dictionary<string, DateTime> _dismissed = new();
    public void DismissNotification(string id)
    {
        lock (_noticeGate)
        {
            PruneDismissed();
            if (_dismissed.Count >= 256) _dismissed.Remove(_dismissed.MinBy(x => x.Value).Key);
            _dismissed[id] = DateTime.UtcNow; Manager.Cancel(id, 0);
        }
    }
    private void PruneDismissed()
    {
        foreach (var old in _dismissed.Where(x => DateTime.UtcNow - x.Value > TimeSpan.FromMinutes(2)).Select(x => x.Key).ToArray()) _dismissed.Remove(old);
    }
    public bool StartupSupported => true;
    public string Description => "Inicio después de reiniciar y desbloquear. Segundo plano mantiene una notificación de servicio. Android puede limitar la batería; el micrófono requiere abrir la app. Los canales del sistema también pueden silenciarse en Ajustes de Android.";
    internal static AppPreferences Preferences(Context context) => new AppPreferencesStore(Path.Combine(context.FilesDir!.AbsolutePath, "ui-settings.json")).Snapshot();
    public void SetStartWithSystem(bool enabled) { /* The boot receiver reads the saved preference before starting. */ }
    private NotificationManager Manager => (NotificationManager)context.GetSystemService(Context.NotificationService)!;
    private AudioManager Audio => (AudioManager)context.GetSystemService(Context.AudioService)!;
    private static string ChannelId(string category) => "quicnet-v2-" + category;
    private NotificationChannel EnsureChannel(string category, bool quiet = false)
    {
        string id = ChannelId(category) + (quiet ? "-quiet" : "");
        if (Manager.GetNotificationChannel(id) is { } existing) return existing;
        string name = category switch { "messages" => "Mensajes", "calls" => "Llamadas", "files" => "Archivos", "screen" => "Pantalla compartida", _ => "Avisos de QuicNet" };
        // Preserve explicit blocking/importance from the previous silent channels.
        var legacy = Manager.GetNotificationChannel("quicnet-" + category);
        var importance = legacy is { } && (legacy.Importance < NotificationImportance.Default || legacy.HasUserSetImportance)
            ? legacy.Importance : category == "calls" ? NotificationImportance.High : NotificationImportance.Default;
        var channel = new NotificationChannel(id, name + (quiet ? " · sin sonido" : ""), quiet && category != "calls" ? NotificationImportance.Low : importance);
        if (quiet) channel.SetSound(null, null);
        else
        {
            // Resource names, rather than generated numeric IDs, survive APK upgrades.
            var uri = Android.Net.Uri.Parse($"android.resource://{context.PackageName}/raw/notice_{category}");
            channel.SetSound(uri, new AudioAttributes.Builder()!.SetUsage(category == "calls" ? AudioUsageKind.NotificationRingtone : AudioUsageKind.Notification)!.SetContentType(AudioContentType.Sonification)!.Build());
            if (OperatingSystem.IsAndroidVersionAtLeast(30) && legacy?.HasUserSetSound == true)
                channel.SetSound(legacy.Sound, legacy.AudioAttributes);
        }
        channel.EnableVibration(!quiet);
        if (!quiet) channel.SetVibrationPattern(category == "calls" ? [0, 300, 180, 300] : [0, 80, 70, 80]);
        channel.LockscreenVisibility = NotificationVisibility.Private;
        Manager.CreateNotificationChannel(channel);
        return Manager.GetNotificationChannel(id)!;
    }
    public NotificationPlatformStatus GetNotificationStatus(string category = "system")
    {
        var channel = EnsureChannel(category);
        bool enabled = Manager.AreNotificationsEnabled() && channel.Importance != NotificationImportance.None;
        bool sound = enabled && channel.Importance >= NotificationImportance.Default && channel.Sound != null &&
            Audio.RingerMode == RingerMode.Normal && Manager.CurrentInterruptionFilter == InterruptionFilter.All &&
            Audio.GetStreamVolume(category == "calls" ? Android.Media.Stream.Ring : Android.Media.Stream.Notification) > 0;
        string message = !enabled ? "Android tiene bloqueadas estas notificaciones. Actívalas en los ajustes del sistema." :
            !sound ? "Los avisos están permitidos, pero Android está silenciando el sonido. Revisa el modo silencio, No molestar, el volumen y el canal." :
            "Android permite estos avisos y su sonido. Puedes cambiar el tono y la vibración de cada categoría en sus ajustes.";
        return new(enabled, sound, true, true, message);
    }
    public bool OpenNotificationSettings()
    {
        if (MainActivity.Foreground is not { } activity) return false;
        activity.RunOnUiThread(() => activity.StartActivity(new Intent(Android.Provider.Settings.ActionAppNotificationSettings)
            .PutExtra(Android.Provider.Settings.ExtraAppPackage, context.PackageName)));
        return true;
    }
    public async Task PlayRingtoneAsync(int volume, CancellationToken ct)
    {
        if (volume <= 0) return;
        var status = GetNotificationStatus("calls");
        var channel = EnsureChannel("calls");
        if (!status.Enabled || channel.Importance < NotificationImportance.Default ||
            Manager.CurrentInterruptionFilter != InterruptionFilter.All || Audio.RingerMode == RingerMode.Silent) return;
        Android.OS.Vibrator? vibrator = null;
        if (channel.ShouldVibrate())
        {
            vibrator = OperatingSystem.IsAndroidVersionAtLeast(31)
                ? ((Android.OS.VibratorManager?)context.GetSystemService(Context.VibratorManagerService))?.DefaultVibrator
#pragma warning disable CA1422
                : (Android.OS.Vibrator?)context.GetSystemService(Context.VibratorService);
#pragma warning restore CA1422
        }
        ct.ThrowIfCancellationRequested();
        using var vibrationStop = ct.Register(() => vibrator?.Cancel());
        try
        {
            if (vibrator?.HasVibrator == true)
            {
                var effect = Android.OS.VibrationEffect.CreateWaveform([0, 300, 180, 300], -1)!;
                if (OperatingSystem.IsAndroidVersionAtLeast(33))
                    vibrator.Vibrate(effect, new Android.OS.VibrationAttributes.Builder()!.SetUsage((int)Android.OS.VibrationAttributesUsageType.Ringtone)!.Build()!);
                else
#pragma warning disable CA1422
                    vibrator.Vibrate(effect, new AudioAttributes.Builder()!.SetUsage(AudioUsageKind.NotificationRingtone)!.Build()!);
#pragma warning restore CA1422
            }
            if (!status.SoundEnabled) { await Task.Delay(1300, ct); return; }
            string path = Path.Combine(context.CacheDir!.AbsolutePath, $"ring-v1-{volume}.wav");
            if (!File.Exists(path)) await File.WriteAllBytesAsync(path, NotificationAudio.Ringtone(volume), ct);
            await PlayAsync(path, channel.Sound, volume, AudioUsageKind.NotificationRingtone, ct);
        }
        finally { vibrator?.Cancel(); }
    }
    public async Task NotifyAsync(AppNotice notice, bool desktop, CancellationToken ct)
    {
        var manager = Manager;
        if (!manager.AreNotificationsEnabled()) return;
        var channel = EnsureChannel(notice.Category);
        if (channel.Importance == NotificationImportance.None) return;
        if (desktop && notice.Popup)
        {
            var intent = new Intent(context, typeof(MainActivity)).PutExtra("quicnetRoute", notice.Url).AddFlags(ActivityFlags.SingleTop | ActivityFlags.ClearTop);
            // Stable identity and unique intent data prevent one alert opening another conversation.
            intent.SetData(Android.Net.Uri.Parse("quicnet://notice/" + Uri.EscapeDataString(notice.Id)));
            var open = PendingIntent.GetActivity(context, 0, intent, PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);
            string selectedChannel = (notice.Sound ? channel.Id : EnsureChannel(notice.Category, quiet: true).Id)!;
            var notification = new Notification.Builder(context, selectedChannel).SetContentTitle(notice.Title).SetContentText(notice.Message)
                .SetStyle(new Notification.BigTextStyle().BigText(notice.Message)).SetVisibility(NotificationVisibility.Private)
                .SetSmallIcon(Resource.Drawable.ic_stat_quicnet).SetAutoCancel(true).SetContentIntent(open)
                .SetOnlyAlertOnce(true).SetTimeoutAfter(notice.Category == "calls" ? 120000 : 0)
                .SetCategory(notice.Category == "calls" ? Notification.CategoryCall : notice.Category == "messages" ? Notification.CategoryMessage : Notification.CategoryEvent).Build();
            lock (_noticeGate)
            {
                PruneDismissed();
                if (_dismissed.ContainsKey(notice.Id) || ct.IsCancellationRequested) return;
                manager.Notify(notice.Id, 0, notification);
            }
            return; // Android owns background audio/vibration, including DND and channel settings.
        }
        if (!notice.Sound || !GetNotificationStatus(notice.Category).SoundEnabled) return;
        string path = Path.Combine(context.CacheDir!.AbsolutePath, $"notification-v2-{notice.Category}-{notice.Tone}-{notice.Volume}.wav");
        if (!File.Exists(path)) await File.WriteAllBytesAsync(path, NotificationAudio.Wave(notice.Tone, notice.Volume, notice.Category), ct);
        await PlayAsync(path, channel.Sound, notice.Volume, AudioUsageKind.Notification, ct);
    }
    private async Task PlayAsync(string path, Android.Net.Uri? selectedSound, int volume, AudioUsageKind usage, CancellationToken ct)
    {
        using var player = new MediaPlayer();
        player.SetAudioAttributes(new AudioAttributes.Builder()!.SetUsage(usage)!.SetContentType(AudioContentType.Sonification)!.Build());
        if (selectedSound != null && !selectedSound.ToString()!.StartsWith($"android.resource://{context.PackageName}/raw/notice_", StringComparison.Ordinal))
        { player.SetDataSource(context, selectedSound); player.SetVolume(volume / 100f, volume / 100f); }
        else player.SetDataSource(path);
        player.Prepare();
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        player.Completion += (_, _) => done.TrySetResult();
        player.Error += (_, args) => { args.Handled = true; done.TrySetException(new IOException("Android no pudo reproducir el aviso.")); };
        ct.ThrowIfCancellationRequested(); player.Start();
        try { await done.Task.WaitAsync(TimeSpan.FromSeconds(5), ct); } finally { player.Stop(); }
    }
}

[BroadcastReceiver(Enabled = true, Exported = true)]
[IntentFilter([Intent.ActionBootCompleted, Intent.ActionMyPackageReplaced])]
public sealed class AppBootReceiver : BroadcastReceiver
{
    public override void OnReceive(Context? context, Intent? intent)
    {
        if (context == null || intent?.Action is not (Intent.ActionBootCompleted or Intent.ActionMyPackageReplaced)) return;
        try
        {
            if (!AndroidAppPlatform.Preferences(context).StartWithSystem) return;
            // connectedDevice is allowed at boot; never request microphone service from a receiver.
            context.StartForegroundService(new Intent(context, typeof(AndroidConnectionService)).PutExtra("fromBoot", true));
        }
        catch (Exception ex) { Android.Util.Log.Warn("QuicNet", "No se pudo iniciar al arrancar: " + ex.Message); }
    }
}
