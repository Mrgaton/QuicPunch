using System.Text.Json.Serialization;
namespace QuicNet.Serialization;

[JsonSerializable(typeof(QuicNet.WebUi.Modules.AppBehaviorSettings))]
[JsonSerializable(typeof(QuicNet.WebUi.Modules.AppSettingsResponse))]
[JsonSerializable(typeof(QuicNet.Notifications.AppNotice))]
[JsonSerializable(typeof(QuicNet.Notifications.NotificationTestResult))]
[JsonSerializable(typeof(QuicNet.Presence.UserPresence))]
[JsonSerializable(typeof(QuicPunch.Multiplexer.QuicPeerMultiplexer.UserPresence), TypeInfoPropertyName = "PeerUserPresence")]
[JsonSerializable(typeof(QuicNet.Networking.PeerLocation))]
[JsonSerializable(typeof(QuicNet.Networking.GeolocationStatus))]
[JsonSerializable(typeof(QuicNet.Networking.GeolocationHistory))]
[JsonSerializable(typeof(QuicNet.WebUi.Modules.GeolocationWorkbenchView))]
internal partial class SharedJsonContext : JsonSerializerContext { }
