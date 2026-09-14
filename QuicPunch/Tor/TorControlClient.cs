#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace QuicPunch;

public sealed class TorControlClient : IAsyncDisposable
{
    private static readonly byte[] ServerSafeCookieKey =
        Encoding.ASCII.GetBytes("Tor safe cookie authentication server-to-controller hash");

    private static readonly byte[] ControllerSafeCookieKey =
        Encoding.ASCII.GetBytes("Tor safe cookie authentication controller-to-server hash");

    private readonly IPAddress _address;
    private readonly int _port;
    private readonly string _cookieFile;
    private readonly SemaphoreSlim _commandLock = new(1, 1);

    private TcpClient? _tcpClient;
    private NetworkStream? _stream;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private int _disposed;

    public TorControlClient(
        int port,
        string cookieFile,
        IPAddress? address = null)
    {
        if (port is < 1 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(port));
        ArgumentException.ThrowIfNullOrWhiteSpace(cookieFile);

        _address = address ?? IPAddress.Loopback;
        _port = port;
        _cookieFile = Path.GetFullPath(cookieFile);
    }

    public bool IsConnected =>
        _tcpClient?.Connected == true && _stream is not null;

    public async ValueTask ConnectAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        await _commandLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsConnected)
                return;

            await ConnectAndAuthenticateCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _commandLock.Release();
        }
    }

    public async ValueTask<TorControlReply> ExecuteAsync(
        string command,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ValidateCommand(command);

        await _commandLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!IsConnected)
                await ConnectAndAuthenticateCoreAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                return await ExecuteCoreAsync(command, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
            {
                // A control connection can disappear during Tor shutdown/restart.
                ResetConnection();
                throw;
            }
        }
        finally
        {
            _commandLock.Release();
        }
    }

    public async ValueTask<string> GetInfoAsync(
        string key,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Any(char.IsWhiteSpace))
            throw new ArgumentException("GETINFO key must be one token.", nameof(key));

        TorControlReply reply =
            await ExecuteAsync("GETINFO " + key, cancellationToken).ConfigureAwait(false);

        foreach (string line in reply.Lines)
        {
            string prefix = key + "=";
            if (line.StartsWith(prefix, StringComparison.Ordinal))
                return line[prefix.Length..];
        }

        return string.Empty;
    }

    public ValueTask<TorControlReply> SignalAsync(
        string signal,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(signal) || signal.Any(char.IsWhiteSpace))
            throw new ArgumentException("Signal must be one token.", nameof(signal));

        return ExecuteAsync("SIGNAL " + signal, cancellationToken);
    }

    public async ValueTask<TorAddOnionResult> AddOnionAsync(
        string keySpec,
        int virtualPort,
        IPEndPoint target,
        bool detach = true,
        bool discardPrivateKey = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keySpec);
        ArgumentNullException.ThrowIfNull(target);
        ValidatePort(virtualPort, nameof(virtualPort));
        ValidatePort(target.Port, nameof(target));

        var flags = new List<string>(2);
        if (detach)
            flags.Add("Detach");
        if (discardPrivateKey)
            flags.Add("DiscardPK");

        string flagPart = flags.Count == 0
            ? string.Empty
            : " Flags=" + string.Join(',', flags);

        string targetHost = target.AddressFamily == AddressFamily.InterNetworkV6
            ? "[" + target.Address + "]"
            : target.Address.ToString();

        string command = string.Create(
            CultureInfo.InvariantCulture,
            $"ADD_ONION {keySpec}{flagPart} Port={virtualPort},{targetHost}:{target.Port}");

        TorControlReply reply = await ExecuteAsync(command, cancellationToken).ConfigureAwait(false);

        string? serviceId = null;
        string? privateKey = null;

        foreach (string line in reply.Lines)
        {
            if (line.StartsWith("ServiceID=", StringComparison.Ordinal))
                serviceId = line["ServiceID=".Length..].Trim();
            else if (line.StartsWith("PrivateKey=", StringComparison.Ordinal))
                privateKey = line["PrivateKey=".Length..].Trim();
        }

        if (string.IsNullOrWhiteSpace(serviceId))
            throw new TorControlException(reply.StatusCode, "ADD_ONION did not return ServiceID.", reply.Lines);

        return new TorAddOnionResult(serviceId, privateKey);
    }

    public async ValueTask DeleteOnionAsync(
        string serviceId,
        CancellationToken cancellationToken = default)
    {
        serviceId = NormalizeServiceId(serviceId);
        await ExecuteAsync("DEL_ONION " + serviceId, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        await _commandLock.WaitAsync().ConfigureAwait(false);
        try
        {
            ResetConnection();
        }
        finally
        {
            _commandLock.Release();
            _commandLock.Dispose();
        }
    }

    private async ValueTask ConnectAndAuthenticateCoreAsync(
        CancellationToken cancellationToken)
    {
        ResetConnection();

        if (!File.Exists(_cookieFile))
            throw new FileNotFoundException("Tor SAFECOOKIE file does not exist.", _cookieFile);

        byte[] cookie = await File.ReadAllBytesAsync(_cookieFile, cancellationToken).ConfigureAwait(false);
        if (cookie.Length != 32)
            throw new InvalidDataException($"Tor control cookie must be exactly 32 bytes, found {cookie.Length}.");

        byte[] clientNonce = RandomNumberGenerator.GetBytes(32);
        byte[]? serverNonce = null;

        try
        {
            var client = new TcpClient(AddressFamily.InterNetwork)
            {
                NoDelay = true
            };
            await client.ConnectAsync(_address, _port, cancellationToken).ConfigureAwait(false);

            NetworkStream stream = client.GetStream();
            var reader = new StreamReader(
                stream,
                new UTF8Encoding(false, true),
                detectEncodingFromByteOrderMarks: false,
                bufferSize: 4096,
                leaveOpen: true);

            var writer = new StreamWriter(
                stream,
                new UTF8Encoding(false),
                bufferSize: 4096,
                leaveOpen: true)
            {
                NewLine = "\r\n",
                AutoFlush = true
            };

            _tcpClient = client;
            _stream = stream;
            _reader = reader;
            _writer = writer;

            await WriteLineCoreAsync(
                "AUTHCHALLENGE SAFECOOKIE " + Convert.ToHexString(clientNonce),
                cancellationToken).ConfigureAwait(false);

            TorControlReply challenge = await ReadReplyCoreAsync(cancellationToken).ConfigureAwait(false);
            EnsureSuccess(challenge);

            string challengeLine = challenge.Lines.Count > 0
                ? challenge.Lines[0]
                : throw new TorControlException(challenge.StatusCode, "Missing AUTHCHALLENGE response.", challenge.Lines);

            Dictionary<string, string> fields = ParseKeyValueFields(challengeLine);
            if (!fields.TryGetValue("SERVERHASH", out string? serverHashHex) ||
                !fields.TryGetValue("SERVERNONCE", out string? serverNonceHex))
            {
                throw new TorControlException(
                    challenge.StatusCode,
                    "AUTHCHALLENGE response is missing SERVERHASH or SERVERNONCE.",
                    challenge.Lines);
            }

            byte[] expectedServerHash = Convert.FromHexString(serverHashHex);
            serverNonce = Convert.FromHexString(serverNonceHex);

            if (expectedServerHash.Length != 32 || serverNonce.Length != 32)
                throw new InvalidDataException("Invalid SAFECOOKIE challenge lengths returned by Tor.");

            byte[] authData = new byte[96];
            cookie.CopyTo(authData, 0);
            clientNonce.CopyTo(authData, 32);
            serverNonce.CopyTo(authData, 64);

            byte[] actualServerHash = HMACSHA256.HashData(ServerSafeCookieKey, authData);
            if (!CryptographicOperations.FixedTimeEquals(expectedServerHash, actualServerHash))
                throw new CryptographicException("Tor SAFECOOKIE server authentication failed.");

            byte[] controllerHash = HMACSHA256.HashData(ControllerSafeCookieKey, authData);

            try
            {
                await WriteLineCoreAsync(
                    "AUTHENTICATE " + Convert.ToHexString(controllerHash),
                    cancellationToken).ConfigureAwait(false);

                TorControlReply authReply = await ReadReplyCoreAsync(cancellationToken).ConfigureAwait(false);
                EnsureSuccess(authReply);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(authData);
                CryptographicOperations.ZeroMemory(actualServerHash);
                CryptographicOperations.ZeroMemory(controllerHash);
                CryptographicOperations.ZeroMemory(expectedServerHash);
            }
        }
        catch
        {
            ResetConnection();
            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(cookie);
            CryptographicOperations.ZeroMemory(clientNonce);
            if (serverNonce is not null)
                CryptographicOperations.ZeroMemory(serverNonce);
        }
    }

    private async ValueTask<TorControlReply> ExecuteCoreAsync(
        string command,
        CancellationToken cancellationToken)
    {
        await WriteLineCoreAsync(command, cancellationToken).ConfigureAwait(false);
        TorControlReply reply = await ReadReplyCoreAsync(cancellationToken).ConfigureAwait(false);
        EnsureSuccess(reply);
        return reply;
    }

    private async ValueTask WriteLineCoreAsync(
        string line,
        CancellationToken cancellationToken)
    {
        StreamWriter writer = _writer ?? throw new InvalidOperationException("Tor control connection is not open.");
        cancellationToken.ThrowIfCancellationRequested();
        await writer.WriteLineAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<TorControlReply> ReadReplyCoreAsync(
        CancellationToken cancellationToken)
    {
        StreamReader reader = _reader ?? throw new InvalidOperationException("Tor control connection is not open.");
        var lines = new List<string>();
        int? expectedCode = null;

        while (true)
        {
            string? raw = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (raw is null)
                throw new EndOfStreamException("Tor control connection closed while reading a reply.");

            if (raw.Length < 4 ||
                !int.TryParse(raw.AsSpan(0, 3), NumberStyles.None, CultureInfo.InvariantCulture, out int code))
            {
                throw new InvalidDataException("Malformed Tor control reply: " + raw);
            }

            expectedCode ??= code;
            if (code != expectedCode.Value)
                throw new InvalidDataException("Tor control reply changed status code mid-response.");

            char separator = raw[3];
            string body = raw.Length > 4 ? raw[4..] : string.Empty;

            if (separator == '+')
            {
                lines.Add(body);
                while (true)
                {
                    string? dataLine = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                    if (dataLine is null)
                        throw new EndOfStreamException("Tor control data block ended unexpectedly.");
                    if (dataLine == ".")
                        break;
                    if (dataLine.StartsWith("..", StringComparison.Ordinal))
                        dataLine = dataLine[1..];
                    lines.Add(dataLine);
                }
                continue;
            }

            if (!string.IsNullOrEmpty(body))
                lines.Add(body);

            if (separator == ' ')
                return new TorControlReply(code, lines);

            if (separator != '-')
                throw new InvalidDataException("Unsupported Tor control reply separator: " + separator);
        }
    }

    private static void EnsureSuccess(TorControlReply reply)
    {
        if (reply.StatusCode is >= 200 and < 300)
            return;

        string message = reply.Lines.Count > 0
            ? string.Join(" | ", reply.Lines)
            : "Tor control command failed.";

        throw new TorControlException(reply.StatusCode, message, reply.Lines);
    }

    private static Dictionary<string, string> ParseKeyValueFields(string line)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string[] tokens = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (string token in tokens)
        {
            int equals = token.IndexOf('=');
            if (equals <= 0 || equals == token.Length - 1)
                continue;
            result[token[..equals]] = token[(equals + 1)..].Trim('"');
        }

        return result;
    }

    private void ResetConnection()
    {
        try { _writer?.Dispose(); } catch { }
        try { _reader?.Dispose(); } catch { }
        try { _stream?.Dispose(); } catch { }
        try { _tcpClient?.Dispose(); } catch { }

        _writer = null;
        _reader = null;
        _stream = null;
        _tcpClient = null;
    }

    private static string NormalizeServiceId(string serviceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceId);
        serviceId = serviceId.Trim().ToLowerInvariant();
        if (serviceId.EndsWith(".onion", StringComparison.Ordinal))
            serviceId = serviceId[..^6];

        if (serviceId.Length != 56)
            throw new ArgumentException("A Tor v3 service id must be 56 characters.", nameof(serviceId));

        return serviceId;
    }

    private static void ValidateCommand(string command)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        if (command.Contains('\r') || command.Contains('\n'))
            throw new ArgumentException("Control command must be a single line.", nameof(command));
    }

    private static void ValidatePort(int port, string paramName)
    {
        if (port is < 1 or > 65535)
            throw new ArgumentOutOfRangeException(paramName);
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
}

public sealed record TorControlReply(
    int StatusCode,
    IReadOnlyList<string> Lines);

public sealed class TorControlException : IOException
{
    public TorControlException(
        int statusCode,
        string message,
        IReadOnlyList<string>? replyLines = null)
        : base($"Tor control error {statusCode}: {message}")
    {
        StatusCode = statusCode;
        ReplyLines = replyLines ?? Array.Empty<string>();
    }

    public int StatusCode { get; }
    public IReadOnlyList<string> ReplyLines { get; }
}