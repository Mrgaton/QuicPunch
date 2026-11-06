using System.Buffers.Binary;
using System.Net;
using System.Text;
using System.Text.Json;
using QuicNet.Protocols;
using QuicNet.Settings;
using QuicPunch;

namespace QuicPunchTests.Tests;

internal static class ProfileAvatarTests
{
    internal static async Task RunAsync(HttpClient client, int port, string csrf, string dataPath)
    {
        byte[] image = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "profile-avatar.avif"));
        if (!ProfileAvatarStore.IsValid(image)) throw new Exception("Real AVIF fixture rejected.");
        byte[] tooWide = (byte[])image.Clone();
        int spatial = image.AsSpan().IndexOf("ispe"u8);
        if (spatial < 0) throw new Exception("Fixture has no spatial extent.");
        BinaryPrimitives.WriteUInt32BigEndian(tooWide.AsSpan(spatial + 8), 513);
        if (ProfileAvatarStore.IsValid(tooWide) || ProfileAvatarStore.IsValid(image.AsSpan(0, image.Length - 1)) || ProfileAvatarStore.IsValid(new byte[ProfileAvatarStore.MaxBytes + 1]))
            throw new Exception("Oversized/non-square/truncated avatars were accepted.");
        byte[] exactLimit = new byte[ProfileAvatarStore.MaxBytes];
        image.CopyTo(exactLimit, 0);
        BinaryPrimitives.WriteUInt32BigEndian(exactLimit.AsSpan(image.Length), (uint)(exactLimit.Length - image.Length));
        "free"u8.CopyTo(exactLimit.AsSpan(image.Length + 4));
        if (!ProfileAvatarStore.IsValid(exactLimit)) throw new Exception("Exact 512 KiB boundary was rejected.");
        byte[] nonsquare = (byte[])image.Clone();
        BinaryPrimitives.WriteUInt32BigEndian(nonsquare.AsSpan(spatial + 12), 511);
        if (ProfileAvatarStore.IsValid(nonsquare)) throw new Exception("Non-square AVIF accepted.");

        string endpoint = $"http://127.0.0.1:{port}/api/profile/avatar";
        using (var denied = await client.PostAsync(endpoint, new ByteArrayContent(image)))
            if (denied.StatusCode != HttpStatusCode.Forbidden) throw new Exception("Avatar upload bypassed CSRF.");
        async Task Post(string suffix, byte[] body, HttpStatusCode expected)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint + suffix);
            request.Headers.Add("X-QuicPunch-CSRF", csrf);
            request.Content = new ByteArrayContent(body);
            using var response = await client.SendAsync(request);
            if (response.StatusCode != expected) throw new Exception("Avatar API response: " + await response.Content.ReadAsStringAsync());
        }
        await Post("", Encoding.UTF8.GetBytes("Not an image"), HttpStatusCode.BadRequest);
        await Post("", tooWide, HttpStatusCode.BadRequest);
        await Post("", new byte[ProfileAvatarStore.MaxBytes + 1], HttpStatusCode.RequestEntityTooLarge);
        await Post("", image, HttpStatusCode.OK);
        using (var response = await client.GetAsync(endpoint))
            if (response.Content.Headers.ContentType?.MediaType != "image/avif" || !(await response.Content.ReadAsByteArrayAsync()).AsSpan().SequenceEqual(image))
                throw new Exception("Saved avatar is not served as its original AVIF bytes.");
        var store = new ProfileAvatarStore(Path.Combine(dataPath, "avatars"));
        if (store.Read()?.AsSpan().SequenceEqual(image) != true) throw new Exception("Avatar did not survive reloading the store.");
        using (var getRemove = await client.GetAsync(endpoint + "/remove"))
            if (getRemove.StatusCode != HttpStatusCode.MethodNotAllowed) throw new Exception("GET deleted an avatar.");
        using (var module = await client.GetAsync($"http://127.0.0.1:{port}/vendor/avif/codec/enc/avif_enc.wasm"))
            if (module.StatusCode != HttpStatusCode.OK || module.Content.Headers.ContentType?.MediaType != "application/wasm")
                throw new Exception("AVIF encoder WASM is not available offline.");

        byte[] animation = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "profile-avatar-animated.avif"));
        if (!ProfileAvatarStore.IsValid(animation)) throw new Exception("Real animated AVIF rejected.");
        byte[] longAnimation = (byte[])animation.Clone();
        int mdhd = longAnimation.AsSpan().IndexOf("mdhd"u8);
        BinaryPrimitives.WriteUInt32BigEndian(longAnimation.AsSpan(mdhd + 20), 10001);
        if (ProfileAvatarStore.IsValid(longAnimation)) throw new Exception("Unbounded animation duration accepted.");
        byte[] badOffset = (byte[])animation.Clone();
        int stco = badOffset.AsSpan().IndexOf("stco"u8);
        BinaryPrimitives.WriteUInt32BigEndian(badOffset.AsSpan(stco + 12), 1);
        if (ProfileAvatarStore.IsValid(badOffset)) throw new Exception("Animation sample offsets were not checked.");
        byte[] audioTrack = (byte[])animation.Clone();
        int handler = audioTrack.AsSpan().LastIndexOf("hdlr"u8);
        "soun"u8.CopyTo(audioTrack.AsSpan(handler + 12));
        if (ProfileAvatarStore.IsValid(audioTrack)) throw new Exception("An audio track was accepted as an avatar.");
        await Post("", longAnimation, HttpStatusCode.BadRequest);
        await Post("", badOffset, HttpStatusCode.BadRequest);
        await Post("", animation, HttpStatusCode.OK);
        using (var response = await client.GetAsync(endpoint))
            if (!(await response.Content.ReadAsByteArrayAsync()).AsSpan().SequenceEqual(animation)) throw new Exception("Animation was flattened or changed during storage.");
        image = animation;
        store = new ProfileAvatarStore(Path.Combine(dataPath, "avatars"));
        if (store.Read()?.AsSpan().SequenceEqual(animation) != true) throw new Exception("Animation did not persist.");

        // Exercise actual chat framing, authenticated sender attribution and Tor isolation.
        var sender = new ChatHandler { Avatars = store };
        using var peer = new PeerInfo().SetCertificateHash(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        using var stream = new MemoryStream();
        using var session = new ChatHandler.ChatSession(peer, stream);
        await sender.SendAvatarAsync(session);
        byte[] frame = stream.ToArray();
        int length = BinaryPrimitives.ReadInt32LittleEndian(frame);
        using var payload = JsonDocument.Parse(frame.AsMemory(4, length));
        var received = new ProfileAvatarStore(Path.Combine(dataPath, "received-avatars"));
        var receiver = new ChatHandler { Avatars = received };
        receiver.ReceiveAvatar(session, payload.RootElement);
        if (received.Read(peer.Id)?.AsSpan().SequenceEqual(image) != true) throw new Exception("Peer did not receive the AVIF.");
        var reopened = new ProfileAvatarStore(Path.Combine(dataPath, "received-avatars"));
        if (reopened.Read(peer.Id)?.AsSpan().SequenceEqual(image) != true) throw new Exception("Peer avatar did not persist.");
        long sent = stream.Length;
        peer.NetworkType = QuicPunch.QuicPunch.NetworkType.Tor;
        await sender.SendAvatarAsync(session);
        if (stream.Length != sent) throw new Exception("Public avatar was transmitted over Tor.");
        using var remove = JsonDocument.Parse("{\"type\":\"peer_avatar\",\"data\":\"\"}");
        receiver.ReceiveAvatar(session, remove.RootElement);
        if (received.Read(peer.Id) == null) throw new Exception("Tor session changed the public avatar.");
        peer.NetworkType = QuicPunch.QuicPunch.NetworkType.Static;
        receiver.ReceiveAvatar(session, remove.RootElement);
        if (received.Read(peer.Id) != null) throw new Exception("Peer avatar deletion was not synchronized.");
        await Post("/remove", [], HttpStatusCode.OK);
        using var missing = await client.GetAsync(endpoint);
        if (missing.StatusCode != HttpStatusCode.NotFound) throw new Exception("Deleted avatar was still served.");
        Console.WriteLine("Static/animated AVIF, 512 KiB bounds, sequence validation, API security, persistence, direct profile sync and Tor privacy passed.");
    }
}
