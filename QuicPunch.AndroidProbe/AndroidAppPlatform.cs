using Android.App;
using Android.Content;
using Android.Media;
using QuicNet.Notifications;
using QuicNet.Settings;

namespace QuicPunch.AndroidProbe;

internal sealed class AndroidAppPlatform(Context context) : IAppPlatform
{
    public bool StartupSupported => true;
    public string Description => "Inicio después de reiniciar y desbloquear. Segundo plano mantiene una notificación de servicio. Android puede limitar la batería; el micrófono requiere abrir la app. Los canales del sistema también pueden silenciarse en Ajustes de Android.";
    internal static AppPreferences Preferences(Context context) => new AppPreferencesStore(Path.Combine(context.FilesDir!.AbsolutePath, "ui-settings.json")).Snapshot();
    public void SetStartWithSystem(bool enabled) { /* The boot receiver reads the saved preference before starting. */ }
    public async Task PlayRingtoneAsync(int volume, CancellationToken ct)
    {
        var manager = (NotificationManager)context.GetSystemService(Context.NotificationService)!;
        if (!manager.AreNotificationsEnabled() || manager.CurrentInterruptionFilter != InterruptionFilter.All ||
            manager.GetNotificationChannel("quicnet-calls")?.Importance == NotificationImportance.None) return;
        string path = Path.Combine(context.CacheDir!.AbsolutePath, $"ring-v1-{volume}.wav");
        if (!File.Exists(path)) await File.WriteAllBytesAsync(path, NotificationAudio.Ringtone(volume), ct);
        using var player = new MediaPlayer();
        player.SetAudioAttributes(new AudioAttributes.Builder()!.SetUsage(AudioUsageKind.NotificationRingtone)!.SetContentType(AudioContentType.Sonification)!.Build());
        player.SetDataSource(path); player.Prepare();
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        player.Completion += (_, _) => done.TrySetResult();
        ct.ThrowIfCancellationRequested(); player.Start();
        try { await done.Task.WaitAsync(TimeSpan.FromSeconds(3), ct); }
        finally { player.Stop(); player.Release(); }
    }
    public async Task NotifyAsync(AppNotice notice, bool desktop, CancellationToken ct)
    {
        var manager = (NotificationManager)context.GetSystemService(Context.NotificationService)!;
        if (!manager.AreNotificationsEnabled()) return;
        string channelId = "quicnet-" + notice.Category;
        string name = notice.Category switch { "messages" => "Mensajes", "calls" => "Llamadas", "files" => "Archivos", "screen" => "Pantalla compartida", _ => "Avisos de QuicNet" };
        var channel = new NotificationChannel(channelId, name, NotificationImportance.Default);
        channel.SetSound(null, null); channel.EnableVibration(false); manager.CreateNotificationChannel(channel);
        if (manager.GetNotificationChannel(channelId)?.Importance == NotificationImportance.None) return;
        if (desktop && notice.Popup)
        {
            var intent = new Intent(context, typeof(MainActivity)).PutExtra("quicnetRoute", notice.Url).AddFlags(ActivityFlags.SingleTop | ActivityFlags.ClearTop);
            int id = StringComparer.Ordinal.GetHashCode(notice.Id) & int.MaxValue;
            var open = PendingIntent.GetActivity(context, id, intent, PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);
            var notification = new Notification.Builder(context, channelId).SetContentTitle(notice.Title).SetContentText(notice.Message)
                .SetSmallIcon(Android.Resource.Drawable.IcDialogInfo).SetAutoCancel(true).SetContentIntent(open)
                .SetCategory(notice.Category == "calls" ? Notification.CategoryCall : Notification.CategoryMessage).Build();
            manager.Notify(id, notification);
        }
        if (!notice.Sound || manager.CurrentInterruptionFilter != InterruptionFilter.All) return;
        string path = Path.Combine(context.CacheDir!.AbsolutePath, $"notification-{notice.Tone}-{notice.Volume}.wav");
        if (!File.Exists(path)) await File.WriteAllBytesAsync(path, NotificationAudio.Wave(notice.Tone, notice.Volume), ct);
        using var player = new MediaPlayer();
        player.SetAudioAttributes(new AudioAttributes.Builder()!.SetUsage(AudioUsageKind.Notification)!.SetContentType(AudioContentType.Sonification)!.Build());
        player.SetDataSource(path); player.Prepare();
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        player.Completion += (_, _) => done.TrySetResult(); player.Start();
        try { await done.Task.WaitAsync(TimeSpan.FromSeconds(2), ct); } finally { player.Stop(); player.Release(); }
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
