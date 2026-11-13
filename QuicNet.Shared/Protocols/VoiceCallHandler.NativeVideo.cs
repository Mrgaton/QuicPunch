using System.Text.Json;
using QuicNet.Serialization;
using QuicNet.Video;
using QuicPunch;

namespace QuicNet.Protocols;

internal sealed partial class VoiceCallHandler
{
    private const byte FrameTypeNativeVideoCapabilities=0x2B;
    public sealed partial class VoiceCallSession
    {
        internal NativeVideoCapabilities? NativeVideoCapabilities {get;set;}
    }
    internal async Task PublishNativeVideoCapabilitiesAsync()
    {
        var caps=NativeVideoRuntime.Instance.Capabilities;
        await SetLocalScreenCodecCapabilitiesAsync(LocalScreenEncoderCodecMask,caps.decoderMask,caps.hardwareMask).ConfigureAwait(false);
        foreach(var session in ActiveCalls.Values)await SendNativeCapabilitiesAsync(session).ConfigureAwait(false);
    }
    private static async Task SendNativeCapabilitiesAsync(VoiceCallSession session)
    {
        var capabilities=await NativeVideoRuntime.Instance.EnsureCapabilitiesAsync().ConfigureAwait(false);
        await session.SendFrameAsync(FrameTypeNativeVideoCapabilities,JsonSerializer.SerializeToUtf8Bytes(capabilities,SharedJson.Options)).ConfigureAwait(false);
    }
    private void ReceiveNativeCapabilities(PeerInfo peer,byte[] payload)
    {
        if(payload.Length>32768)return;
        try
        {
            var caps=JsonSerializer.Deserialize<NativeVideoCapabilities>(payload,SharedJson.Options);
            if(caps==null||caps.engine==null||caps.engine.Length>128||caps.decoders==null||caps.decoders.Length>16||caps.decoders.Any(d=>d==null||d.codecId is not (0 or 1 or 3 or 4 or 5)||d.reason==null||d.reason.Length>1024||d.codec==null||d.codec.Length>128||d.status is not ("hardware" or "software" or "unsupported" or "unknown")||d.component?.Length>256||d.testWidth is < 0 or > 7680||d.testHeight is < 0 or > 4320))return;
            if(ActiveCalls.TryGetValue(peer.Id,out var session))
            {
                session.NativeVideoCapabilities=caps;
                OnScreenCodecCapabilitiesReceived?.Invoke(peer,session.RemoteScreenEncoderCodecMask,session.RemoteScreenDecoderCodecMask);
            }
        }
        catch(JsonException){}
    }
    internal async Task ReplyNativeDecoderProbeAsync(PeerInfo peer,JsonElement probe)
    {
        try
        {
            var request=probe.Deserialize<ScreenDecoderProbeMessage>(SharedJson.Options);
            if(request==null)return;
            var result=await NativeVideoRuntime.Instance.CheckProfileAsync(request.codec,request.width,request.height,request.description).ConfigureAwait(false);
            var diagnostic=JsonSerializer.SerializeToElement(new ScreenDecoderDiagnostic(request.codec,request.width,request.height,result.Mode,result.Reason),SharedJson.Options);
            await ReplyScreenDecoderProbeAsync(peer.Id,request.requestId,result.Supported,diagnostic).ConfigureAwait(false);
        }
        catch(Exception ex){QuicPunchLog.Info("[NATIVE VIDEO] Probe failed: "+ex.Message);}
    }
}
