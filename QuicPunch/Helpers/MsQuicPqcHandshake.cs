namespace QuicPunch.Helpers;
internal static class MsQuicPqcHandshake
{
    internal const uint X25519MlKem768 = 4588;
    // System.Net.Quic has no public negotiated-group API. Owned bridge telemetry does.
    internal static bool TryGetNegotiatedGroup(System.Net.Quic.QuicConnection connection, out uint group) { group = 0; return false; }
}
