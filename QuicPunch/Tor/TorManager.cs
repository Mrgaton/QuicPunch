#nullable enable

using System;
using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace QuicPunch;

public sealed class TorManager : IAsyncDisposable
{
    private readonly TorRuntimeManager _runtime;
    private readonly bool _ownsRuntime;
    private int _disposed;

    public TorManager(TorRuntimeManager runtime, bool ownsRuntime = false)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        if (!runtime.IsRunning)
            throw new InvalidOperationException("TorRuntimeManager must be started before creating TorManager.");
        _ownsRuntime = ownsRuntime;
    }

    public TorRuntimeManager Runtime => _runtime;
    public int SocksPort => _runtime.SocksPort;
    public int ControlPort => _runtime.ControlPort;

    public static async ValueTask<TorManager> StartPortableAsync(
        TorRuntimeOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        TorRuntimeManager runtime =
            await TorRuntimeManager.StartPortableAsync(options, cancellationToken).ConfigureAwait(false);
        return new TorManager(runtime, ownsRuntime: true);
    }

    public async ValueTask<TorTcpConnection> ConnectAsync(
        string host,
        int port,
        string? isolationKey = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        host = NormalizeHost(host);
        ValidatePort(port, nameof(port));

        byte[] hostBytes = Encoding.ASCII.GetBytes(host);
        if (hostBytes.Length is < 1 or > 255)
            throw new ArgumentException("SOCKS5 hostname must be 1..255 ASCII bytes.", nameof(host));

        var client = new TcpClient(AddressFamily.InterNetwork)
        {
            NoDelay = true
        };

        try
        {
            await client.ConnectAsync(IPAddress.Loopback, SocksPort, cancellationToken).ConfigureAwait(false);
            client.NoDelay = true;
            NetworkStream stream = client.GetStream();

            if (string.IsNullOrEmpty(isolationKey))
            {
                await stream.WriteAsync(new byte[] { 0x05, 0x01, 0x00 }, cancellationToken).ConfigureAwait(false);
                byte[] selection = new byte[2];
                await ReadExactlyAsync(stream, selection, cancellationToken).ConfigureAwait(false);
                if (selection[0] != 0x05 || selection[1] != 0x00)
                    throw new IOException($"Tor SOCKS5 rejected no-auth negotiation (method 0x{selection[1]:X2}).");
            }
            else
            {
                // Tor 0.4.9 typed SOCKS-auth extension, format 0:
                // username = "<torS0X>0", password = stream isolation parameter.
                byte[] username = Encoding.ASCII.GetBytes("<torS0X>0");
                byte[] password = Encoding.UTF8.GetBytes(isolationKey);
                if (password.Length > 255)
                    throw new ArgumentException("Tor SOCKS isolation key must encode to at most 255 bytes.", nameof(isolationKey));

                await stream.WriteAsync(new byte[] { 0x05, 0x01, 0x02 }, cancellationToken).ConfigureAwait(false);
                byte[] selection = new byte[2];
                await ReadExactlyAsync(stream, selection, cancellationToken).ConfigureAwait(false);
                if (selection[0] != 0x05 || selection[1] != 0x02)
                    throw new IOException($"Tor SOCKS5 rejected username/password isolation negotiation (method 0x{selection[1]:X2}).");

                byte[] auth = new byte[3 + username.Length + password.Length];
                int p = 0;
                auth[p++] = 0x01;
                auth[p++] = checked((byte)username.Length);
                username.CopyTo(auth, p);
                p += username.Length;
                auth[p++] = checked((byte)password.Length);
                password.CopyTo(auth, p);

                await stream.WriteAsync(auth, cancellationToken).ConfigureAwait(false);
                byte[] authReply = new byte[2];
                await ReadExactlyAsync(stream, authReply, cancellationToken).ConfigureAwait(false);
                if (authReply[0] != 0x01 || authReply[1] != 0x00)
                    throw new IOException("Tor SOCKS5 stream-isolation authentication failed.");
            }

            byte[] request = new byte[7 + hostBytes.Length];
            int o = 0;
            request[o++] = 0x05; // VER
            request[o++] = 0x01; // CONNECT
            request[o++] = 0x00; // RSV
            request[o++] = 0x03; // DOMAINNAME
            request[o++] = checked((byte)hostBytes.Length);
            hostBytes.CopyTo(request, o);
            o += hostBytes.Length;
            BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(o, 2), checked((ushort)port));

            await stream.WriteAsync(request, cancellationToken).ConfigureAwait(false);

            byte[] header = new byte[4];
            await ReadExactlyAsync(stream, header, cancellationToken).ConfigureAwait(false);
            if (header[0] != 0x05)
                throw new IOException("Invalid SOCKS5 response version from Tor.");
            if (header[1] != 0x00)
                throw new TorSocksException(header[1], DescribeSocksError(header[1]));
            if (header[2] != 0x00)
                throw new IOException("Invalid SOCKS5 reserved byte from Tor.");

            await ConsumeSocksBoundAddressAsync(stream, header[3], cancellationToken).ConfigureAwait(false);

            return new TorTcpConnection(
                client,
                remoteOnion: host.EndsWith(".onion", StringComparison.OrdinalIgnoreCase) ? host : null,
                remoteVirtualPort: port,
                inbound: false);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    public ValueTask<TorTcpConnection> ConnectOnionAsync(
        string onionAddress,
        int port,
        string? isolationKey = null,
        CancellationToken cancellationToken = default)
    {
        string host = NormalizeOnion(onionAddress);
        return ConnectAsync(host, port, isolationKey, cancellationToken);
    }

    public TorIdentity CreateRandomIdentity() => TorIdentity.CreateRandom();

    public TorIdentity CreateDeterministicIdentity(
        string secret,
        string context = "QuicPunch/default",
        int iterations = 600_000) =>
        TorIdentity.CreateDeterministic(secret, context, iterations);

    public async ValueTask<TorIdentity> GenerateIdentityWithTorAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            int localPort = ((IPEndPoint)listener.LocalEndpoint).Port;
            TorAddOnionResult result = await _runtime.ControlClient
                .AddOnionAsync(
                    "NEW:ED25519-V3",
                    virtualPort: 1,
                    new IPEndPoint(IPAddress.Loopback, localPort),
                    detach: true,
                    discardPrivateKey: false,
                    cancellationToken)
                .ConfigureAwait(false);

            try
            {
                if (string.IsNullOrWhiteSpace(result.PrivateKeySpec))
                    throw new InvalidOperationException("Tor did not return the generated ED25519-V3 private key.");

                TorIdentity identity = TorIdentity.ParseTorPrivateKey(result.PrivateKeySpec);
                if (!string.Equals(identity.ServiceId, result.ServiceId, StringComparison.OrdinalIgnoreCase))
                    throw new CryptographicException("Tor-generated private key does not match returned ServiceID.");

                return identity;
            }
            finally
            {
                await DeleteOnionAsync(result.ServiceId, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            listener.Stop();
        }
    }

    public async ValueTask<TorOnionService> CreateOnionServiceAsync(
        int virtualPort = 443,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ValidatePort(virtualPort, nameof(virtualPort));

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();

        try
        {
            int localPort = ((IPEndPoint)listener.LocalEndpoint).Port;
            TorAddOnionResult result = await _runtime.ControlClient
                .AddOnionAsync(
                    "NEW:ED25519-V3",
                    virtualPort,
                    new IPEndPoint(IPAddress.Loopback, localPort),
                    detach: true,
                    discardPrivateKey: false,
                    cancellationToken)
                .ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(result.PrivateKeySpec))
                throw new InvalidOperationException("Tor did not return the generated onion-service private key.");

            TorIdentity identity = TorIdentity.ParseTorPrivateKey(result.PrivateKeySpec);
            if (!string.Equals(identity.ServiceId, result.ServiceId, StringComparison.OrdinalIgnoreCase))
                throw new CryptographicException("Tor-generated private key does not match returned ServiceID.");

            return new TorOnionService(this, identity, virtualPort, listener);
        }
        catch
        {
            listener.Stop();
            throw;
        }
    }

    public async ValueTask<TorOnionService> HostOnionAsync(
        TorIdentity identity,
        int virtualPort = 443,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(identity);
        ValidatePort(virtualPort, nameof(virtualPort));

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();

        try
        {
            int localPort = ((IPEndPoint)listener.LocalEndpoint).Port;
            string keySpec = "ED25519-V3:" + identity.PrivateKeyBase64;

            TorAddOnionResult result = await _runtime.ControlClient
                .AddOnionAsync(
                    keySpec,
                    virtualPort,
                    new IPEndPoint(IPAddress.Loopback, localPort),
                    detach: true,
                    discardPrivateKey: true,
                    cancellationToken)
                .ConfigureAwait(false);

            if (!string.Equals(identity.ServiceId, result.ServiceId, StringComparison.OrdinalIgnoreCase))
            {
                await DeleteOnionAsync(result.ServiceId, cancellationToken).ConfigureAwait(false);
                throw new CryptographicException(
                    "ADD_ONION returned a ServiceID different from the supplied deterministic identity.");
            }

            return new TorOnionService(this, identity, virtualPort, listener);
        }
        catch
        {
            listener.Stop();
            throw;
        }
    }

    public ValueTask DeleteOnionAsync(
        string serviceIdOrOnion,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return _runtime.ControlClient.DeleteOnionAsync(serviceIdOrOnion, cancellationToken);
    }

    public async ValueTask RequestNewCircuitsAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _runtime.ControlClient.SignalAsync("NEWNYM", cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        if (_ownsRuntime)
            await _runtime.DisposeAsync().ConfigureAwait(false);
    }

    private static async ValueTask ConsumeSocksBoundAddressAsync(
        Stream stream,
        byte atyp,
        CancellationToken cancellationToken)
    {
        int addressLength = atyp switch
        {
            0x01 => 4,
            0x04 => 16,
            0x03 => -1,
            _ => throw new IOException($"Tor returned unsupported SOCKS5 ATYP 0x{atyp:X2}.")
        };

        if (addressLength == -1)
        {
            byte[] length = new byte[1];
            await ReadExactlyAsync(stream, length, cancellationToken).ConfigureAwait(false);
            addressLength = length[0];
        }

        byte[] tail = new byte[addressLength + 2]; // address + port
        await ReadExactlyAsync(stream, tail, cancellationToken).ConfigureAwait(false);
    }

    internal static async ValueTask ReadExactlyAsync(
        Stream stream,
        Memory<byte> destination,
        CancellationToken cancellationToken)
    {
        if (stream == null)
            throw new ArgumentNullException(nameof(stream));

        int offset = 0;
        while (offset < destination.Length)
        {
            int read;
            try
            {
                read = await stream.ReadAsync(destination[offset..], cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is NullReferenceException or ObjectDisposedException or SocketException or IOException)
            {
                read = 0;
            }

            if (read == 0)
                throw new EndOfStreamException("Stream closed before the requested bytes were received.");

            offset += read;
        }
    }

    internal static async ValueTask WriteAllAsync(
        Stream stream,
        ReadOnlyMemory<byte> source,
        CancellationToken cancellationToken)
    {
        await stream.WriteAsync(source, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public static string NormalizeOnion(string onionAddress)
    {
        string host = NormalizeHost(onionAddress);
        if (!host.EndsWith(".onion", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Expected a .onion hostname.", nameof(onionAddress));

        string serviceId = host[..^6];
        if (serviceId.Length != 56)
            throw new ArgumentException("Expected a 56-character Tor v3 service id.", nameof(onionAddress));

        foreach (char c in serviceId)
        {
            bool ok = c is >= 'a' and <= 'z' or >= '2' and <= '7';
            if (!ok)
                throw new ArgumentException("Invalid Tor v3 .onion Base32 characters.", nameof(onionAddress));
        }

        return host;
    }

    internal static string NormalizeServiceId(string onionOrServiceId)
    {
        string host = NormalizeHost(onionOrServiceId);
        if (host.EndsWith(".onion", StringComparison.OrdinalIgnoreCase))
            host = host[..^6];
        if (host.Length != 56)
            throw new ArgumentException("Expected a Tor v3 56-character service id.", nameof(onionOrServiceId));
        return host;
    }

    private static string NormalizeHost(string host)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        host = host.Trim().ToLowerInvariant();

        if (Uri.TryCreate(host, UriKind.Absolute, out Uri? uri))
            host = uri.Host.ToLowerInvariant();

        if (host.EndsWith('.', StringComparison.Ordinal))
            host = host[..^1];

        if (host.Length == 0 || host.Any(char.IsWhiteSpace))
            throw new ArgumentException("Invalid hostname.", nameof(host));

        // SOCKS domain form is ASCII. Onion hostnames are ASCII by definition.
        if (host.Any(c => c > 0x7F))
            throw new ArgumentException("Hostname must be ASCII for this Tor SOCKS client.", nameof(host));

        return host;
    }

    private static string DescribeSocksError(byte code) => code switch
    {
        0x01 => "General SOCKS server failure.",
        0x02 => "Connection not allowed by ruleset.",
        0x03 => "Network unreachable.",
        0x04 => "Host unreachable.",
        0x05 => "Connection refused.",
        0x06 => "TTL expired.",
        0x07 => "SOCKS command not supported.",
        0x08 => "SOCKS address type not supported.",
        0xF0 => "Tor onion-service descriptor not found / invalid onion address.",
        0xF1 => "Tor onion service descriptor invalid.",
        0xF2 => "Tor onion service introduction failed.",
        0xF3 => "Tor onion service rendezvous failed.",
        0xF4 => "Tor onion service missing client authorization.",
        0xF5 => "Tor onion service authorization type unsupported.",
        0xF6 => "Tor onion service descriptor retrieval timed out.",
        _ => $"SOCKS/Tor failure 0x{code:X2}."
    };

    private static void ValidatePort(int port, string name)
    {
        if (port is < 1 or > 65535)
            throw new ArgumentOutOfRangeException(name);
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
}

public sealed class TorSocksException : IOException
{
    public TorSocksException(byte replyCode, string message)
        : base($"Tor SOCKS5 error 0x{replyCode:X2}: {message}")
    {
        ReplyCode = replyCode;
    }

    public byte ReplyCode { get; }
}
