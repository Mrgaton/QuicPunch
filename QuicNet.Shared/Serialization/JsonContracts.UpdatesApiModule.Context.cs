#nullable enable
using System.Text.Json.Serialization;
namespace QuicNet.Serialization;

[JsonSerializable(typeof(global::QuicNet.Serialization.UpdatesApiModuleMessage1))]
[JsonSerializable(typeof(global::QuicNet.Updates.UpdateStatus))]
[JsonSerializable(typeof(object))]
internal partial class SharedJsonContext : JsonSerializerContext { }
