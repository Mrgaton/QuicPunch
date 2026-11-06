using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace QuicPunch.Helpers;

public sealed partial class PeerStore
{
    [JsonSourceGenerationOptions(WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonSerializable(typeof(Db))]
    private partial class PeerStoreJsonContext : JsonSerializerContext { }

    // In-memory fixtures only: no user store is opened or modified by this check.
    internal static void VerifyAotJsonContract()
    {
        var sample = new Db { SavedPeers = [
            new Row(),
            new Row { Ips = ["127.0.0.1", "::1"], CertHash = "fixture", Ports = [1, 65535],
                PreferredPort = 49123, MinPort = 1, MaxPort = 65535, Name = "á<&\"", OnionAddress = "fixture.onion",
                AutoConnect = false, Trusted = false, AutomaticPermissions = ["chat", "vpn"],
                Profile = PeerProfile.Administrator, EcdhPublicKey = "fixture", NetworkType = 2,
                ConnectionFlags = 255, NostrPubKey = "fixture", ResumptionTicket = "fixture",
                LastAuthenticatedActivityUtc = new DateTime(2026, 10, 23, 12, 0, 0, DateTimeKind.Utc) },
            new Row { Ip = "127.0.0.1", Port = 443, CertHash = "legacy-fixture" }
        ] };
        byte[] generated = JsonSerializer.SerializeToUtf8Bytes(sample, PeerStoreJsonContext.Default.Db);
        var decoded = JsonSerializer.Deserialize(generated, PeerStoreJsonContext.Default.Db)!;
        if (!generated.AsSpan().SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(decoded, PeerStoreJsonContext.Default.Db)))
            throw new InvalidDataException("Peer store AOT round-trip changed the JSON.");
        if (RuntimeFeature.IsDynamicCodeSupported)
        {
            var originalOptions = new JsonSerializerOptions { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
            byte[] original = JsonSerializer.SerializeToUtf8Bytes(sample, originalOptions);
            if (!original.AsSpan().SequenceEqual(generated))
                throw new InvalidDataException("Generated peer store JSON differs from the existing format.");
            const string legacy = "{\"Version\":1,\"SavedPeers\":[{\"Ip\":\"127.0.0.1\",\"Port\":443,\"CertHash\":\"fixture\"}]}";
            var oldReader = JsonSerializer.Deserialize<Db>(legacy, originalOptions);
            var newReader = JsonSerializer.Deserialize(legacy, PeerStoreJsonContext.Default.Db);
            if (!JsonSerializer.SerializeToUtf8Bytes(oldReader, originalOptions).AsSpan()
                .SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(newReader, PeerStoreJsonContext.Default.Db)))
                throw new InvalidDataException("Generated peer store reader differs on legacy data.");
        }
    }
}
