using System.Text;

public static class Base64Url
{
    public static string ToBase64Url(this string data) => Convert.ToBase64String(Encoding.UTF8.GetBytes(data)).Trim('=').Replace('+', '-').Replace('/', '_');
    public static string ToBase64Url(this byte[] data) => Convert.ToBase64String(data).Trim('=').Replace('+', '-').Replace('/', '_');

    public static byte[] FromBase64Url(this string data) => Convert.FromBase64String(data.Replace('_', '/').Replace('-', '+').PadRight(data.Length + (4 - data.Length % 4) % 4, '='));
}
