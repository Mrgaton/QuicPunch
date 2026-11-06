using Rage.Network;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;
public static class SslPinning
{
    public const string DCPDomain = "dcp.gato.ovh";

    private static readonly object _hashLock = new object();
    public static HMACSHA3_512 Hasher = new HMACSHA3_512([169, 93, 73, 101, 170, 191, 94, 22, 123, 138, 116, 234, 65, 183, 128, 144, 144, 98, 160, 183, 236, 208, 183, 185, 122, 168, 73, 61, 212, 129, 61, 27, 145, 198, 160, 236, 146, 164, 85, 240, 202, 59, 61, 20, 26, 157, 189, 101, 172, 213, 152, 244, 45, 253, 34, 204, 176, 167, 181, 198, 57, 111, 248, 231, 189, 161, 141, 64, 16, 50, 157, 171, 52, 101, 139, 187, 104, 224, 71, 48, 81, 101, 66, 24, 9, 124, 212, 242, 149, 181, 216, 165, 127, 94, 250, 210, 167, 251, 124, 81, 240, 163, 40, 163, 69, 166, 55, 28, 224, 177, 156, 91, 149, 71, 156, 29, 9, 53, 153, 136, 208, 202, 134, 122, 64, 48, 32, 138, 177]);
    public static byte[] Compute(byte[] data)
    {
        if (data == null)
            throw new ArgumentNullException(nameof(data));

        lock (_hashLock)
        {
            return Hasher.ComputeHash(data); // seguro mientras todo pase por este lock
        }
    }

    private static readonly string[] SoftenedDomains = ["translate.googleapis.com", "kekma.net"];

    private static ConcurrentDictionary<string, byte[]> ServersCerts = new()
    {
        [DCPDomain] = [230, 120, 25, 254, 201, 118, 215, 175, 123, 241, 204, 178, 29, 248, 10, 221, 63, 38, 124, 172, 203, 221, 238, 192, 177, 128, 200, 156, 189, 199, 243, 63, 22, 196, 149, 144, 102, 36, 23, 220, 48, 132, 197, 111, 169, 155, 23, 169, 109, 157, 165, 70, 219, 28, 124, 243, 166, 72, 56, 57, 3, 62, 11, 209],
    };

    /*private static byte[] GetCertPubKey(string hostname)
    {
        if (ServersCerts.TryGetValue(hostname, out var cert)) return cert;

        using (HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Post, $"https://{ServersCerts.ElementAt(0).Key}/getcert/" + hostname))
        {
            using (HttpResponseMessage res = Net.client.SendAsync(req, new CancellationTokenSource(TimeSpan.FromSeconds(20)).Token).Result)
            {
                string content = res.Content.ReadAsStringAsync().Result;

                JsonArray el = JsonNode.Parse(content)["pubkey"]["data"].AsArray();

                int keySize = el.Count;
                byte[] key = new byte[keySize];
                int i = 0;
                foreach (var e in el) key[i++] = e.GetValue<byte>();

                ServersCerts.Add(hostname, key);

                return key;
            }
        }
    }*/

    private static byte[] GetCertPubKey(string hostname, bool noCache = false)
    {
        if (ServersCerts.TryGetValue(hostname, out var cert))
            return cert;

        try
        { //using (HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Get, $"https://{ServersCerts.ElementAt(0).Key}/cert/" + hostname))
            using (HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Get, $"https://{DCPDomain}:62569/cert/" + hostname))
            {
                req.Version = HttpVersion.Version20;

                if (noCache)
                {
                    req.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue
                    {
                        NoCache = true,
                        NoStore = true
                    };
                }

                using (HttpResponseMessage res = Net.client.SendAsync(req, new CancellationTokenSource(TimeSpan.FromSeconds(15)).Token).Result)
                {
                    string content = res.Content.ReadAsStringAsync().Result;

#if DEBUG
                    Console.WriteLine(AnsiHelper.AnsiColors.Gold + content);
#endif

                    string b64pkey = (string)JsonNode.Parse(content)["PublicKey"];

                    var key = Compute(Convert.FromBase64String(b64pkey));

                    ServersCerts.TryAdd(hostname, key);

                    return key;
                }
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.ToString());
            throw;
        }
    }

    private static void LogErrors(X509Certificate certificate, string description)
    {
        Console.WriteLine();
        Console.WriteLine(AnsiHelper.AnsiColors.DarkPurple + "Error validating certificate:");
        Console.WriteLine(AnsiHelper.AnsiColors.LightRed + certificate.Issuer);
        Console.WriteLine(AnsiHelper.AnsiColors.LightRed + certificate.Subject);
        Console.WriteLine(AnsiHelper.AnsiColors.BrightRed + description);
    }

    public static bool Verify(object sender, X509Certificate certificate, X509Chain chain, SslPolicyErrors sslPolicyErrors)
    {
        using (X509Certificate2 remoteCertificate = new X509Certificate2(certificate))
        {
            if (DateTime.Now > remoteCertificate.NotAfter && DateTime.Now < remoteCertificate.NotBefore)
            {
#if DEBUG
                LogErrors(certificate, "Error cert expired");
#endif
                return false;
            }

            var request = sender as HttpRequestMessage;

            string expectedHostname = request.Headers.Host ?? request.RequestUri.Host;

            byte[] publicKey = Compute(remoteCertificate.PublicKey.EncodedKeyValue.RawData);

            bool softened = SoftenedDomains.Contains(expectedHostname);

            if (!softened && ServersCerts.ContainsKey(expectedHostname))
            {
                byte[] expectedCert = ServersCerts[expectedHostname];

                bool certValid = publicKey.SequenceEqual(expectedCert.Skip(expectedCert.Length - publicKey.Length));

                if (!certValid)
                {
#if DEBUG
                    LogErrors(certificate,
                        "Expected: [" + string.Join(", ", expectedCert.Select(b => b.ToString())) + "]\n" +
                        "But Gott: [" + string.Join(", ", publicKey.Select(b => b.ToString())) + ']');
#endif

                    //return false;

                    throw new AccessViolationException();
                }

                return certValid;
            }

            if (!remoteCertificate.Verify())
            {
#if DEBUG
                LogErrors(certificate, "Error cert not verified");
#endif
                return false;
            }

            if (sslPolicyErrors != SslPolicyErrors.None)
            {
#if DEBUG
                LogErrors(certificate, "Error in policy:" + sslPolicyErrors);
#endif
                return false;
            }

#if DEBUG
            Console.WriteLine("Verifying CERT:" + (request.Headers.Host ?? request.RequestUri.ToString()));
#endif

            /*if (!softened)
            {
            retryWithoutCache:

                bool fetchWithoutCache = false;

                byte[] remoteKey = GetCertPubKey(expectedHostname, fetchWithoutCache);

                if (!remoteKey.Skip(remoteKey.Length - publicKey.Length).SequenceEqual(publicKey))
                {
                    if (!fetchWithoutCache)
                    {
                        fetchWithoutCache = true;

                        goto retryWithoutCache;
                    }

#if DEBUG
                    LogErrors(certificate,
                        "Expected: [" + string.Join(", ", remoteKey.Select(b => b.ToString())) + "]\n" +
                        "But Gott: [" + string.Join(", ", publicKey.Select(b => b.ToString())) + ']');
#endif
                    return false;
                }
            }*/

            return true;
        }
    }
}
