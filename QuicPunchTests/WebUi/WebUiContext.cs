using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace QuicPunchTests.WebUi;

public static class WebUiContext
{
    public const int MaxConcurrentHttpRequests = 32;
    public const int MaxJsonBodyBytes = 128 * 1024;
    public const int MaxChatBodyBytes = 32 * 1024 * 1024;
    public const int MaxVoiceBodyBytes = 1024 * 1024;
    public const int MaxClipboardChars = 64 * 1024;
    public const long MaxChatHistoryBytes = 128L * 1024 * 1024;

    public const int MaxScreenShareBodyBytes = 2 * 1024 * 1024; // 2MB max for high-res JPEG frame

    public static long GetRequestBodyLimit(string path) => path switch
    {
        "/api/voice-send" => MaxVoiceBodyBytes,
        "/api/voice/screen-share/frame" => MaxScreenShareBodyBytes,
        "/api/chat-send" => MaxChatBodyBytes,
        "/api/chat/share-file" => 100L * 1024 * 1024 * 1024,
        _ => MaxJsonBodyBytes
    };

    public static async Task<byte[]> ReadRequestBytesAsync(HttpListenerRequest req, int maxBytes)
    {
        if (req.ContentLength64 > maxBytes) throw new InvalidDataException("Request body is too large.");
        using var stream = new MemoryStream();
        byte[] buffer = new byte[8192];
        int total = 0;
        while (true)
        {
            int read = await req.InputStream.ReadAsync(buffer.AsMemory(0, buffer.Length)).ConfigureAwait(false);
            if (read == 0) break;
            total += read;
            if (total > maxBytes) throw new InvalidDataException("Request body is too large.");
            await stream.WriteAsync(buffer.AsMemory(0, read)).ConfigureAwait(false);
        }
        return stream.ToArray();
    }

    public static async Task<JsonDocument> ReadJsonAsync(HttpListenerRequest req, string path, bool allowEmpty = false)
    {
        byte[] bytes = await ReadRequestBytesAsync(req, checked((int)Math.Min(int.MaxValue, GetRequestBodyLimit(path)))).ConfigureAwait(false);
        if (bytes.Length == 0 && allowEmpty) return JsonDocument.Parse("{}");
        return JsonDocument.Parse(bytes);
    }

    public static async Task WriteJsonAsync(HttpListenerResponse resp, object value, int statusCode = 200)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value);
        resp.StatusCode = statusCode;
        resp.ContentType = "application/json; charset=utf-8";
        resp.ContentLength64 = bytes.Length;
        await resp.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
    }

    public static Guid ParseGuid(JsonElement root, string property)
    {
        string value = root.GetProperty(property).GetString() ?? "";
        if (!Guid.TryParse(value, out var guid)) throw new InvalidDataException($"Invalid {property}.");
        return guid;
    }

    public static byte[] ParseCertHash(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            throw new InvalidDataException("Certificate hash cannot be empty.");
        input = input.Trim();
        try
        {
            if (input.Length == 64 && input.All(Uri.IsHexDigit))
                return Convert.FromHexString(input);
        }
        catch { }

        try
        {
            return Convert.FromBase64String(input);
        }
        catch
        {
            return System.Buffers.Text.Base64Url.DecodeFromChars(input);
        }
    }
}
