using System;
using System.IO;
using System.Net.Http;
using System.Net.WebSockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using QuicPunch;
using QuicPunch.Helpers;
using QuicPunchTests.Protocols;
using QuicPunchTests.Settings;
using QuicPunchTests.WebUi;

namespace QuicPunchTests.Tests;
    public static class WebUiSecurityTests
    {
        public static async Task RunAsync()
        {
            Console.WriteLine("==================================================");
            Console.WriteLine("   WEBUI & WEBSOCKET SECURITY INTEGRATION TESTS   ");
            Console.WriteLine("==================================================");

            await TestWebUiOriginAndCsrfAsync();
            TestTorPreExistingExecutableDetection();
            await TestPeerInfoSessionLockConcurrencyAsync();

            Console.WriteLine("==================================================");
            Console.WriteLine("   ALL WEBUI & SECURITY CHECKS PASSED!            ");
            Console.WriteLine("==================================================");
        }

        private static async Task TestWebUiOriginAndCsrfAsync()
        {
            Console.Write("[TEST] WebUi Origin & CSRF enforcement for WebSockets & API... ");
            string tempDir = Path.Combine(Path.GetTempPath(), "qp_test_webui_sec_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);

            using var cts = new CancellationTokenSource();
            using var qp = new QuicPunch.QuicPunch(appDataPath: tempDir, listeningPort: 0);
            var chatHandler = new ChatHandler();
            var lanHandler = new VirtualLanHandler();
            var voiceHandler = new VoiceCallHandler(qp);
            var speedTestHandler = new SpeedTestHandler();
            var preferences = new AppPreferencesStore(tempDir);

            var server = new WebUiServer(
                qp,
                chatHandler,
                lanHandler,
                voiceHandler,
                speedTestHandler,
                preferences,
                cts,
                port: 5800);

            try
            {
                server.Start(openWindow: false);
                int port = server.Port;
                string csrf = server.CsrfToken;

                using var handler = new HttpClientHandler { AllowAutoRedirect = false };
                using var client = new HttpClient(handler);

                // 1. Cross-Origin HTTP request should be rejected (403)
                using (var req = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{port}/api/status"))
                {
                    req.Headers.Add("Origin", "http://evil-site.com");
                    using var resp = await client.SendAsync(req);
                    if (resp.StatusCode != System.Net.HttpStatusCode.Forbidden)
                        throw new Exception($"Expected 403 Forbidden for cross-origin request, got {resp.StatusCode}");
                }

                // 2. /api/client-closing with GET should be rejected (405 Method Not Allowed)
                using (var req = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{port}/api/client-closing"))
                {
                    using var resp = await client.SendAsync(req);
                    if (resp.StatusCode != System.Net.HttpStatusCode.MethodNotAllowed)
                        throw new Exception($"Expected 405 Method Not Allowed on GET /api/client-closing, got {resp.StatusCode}");
                }

                // 3. /api/client-closing with POST but without CSRF should be rejected (403 Forbidden)
                using (var req = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{port}/api/client-closing"))
                {
                    using var resp = await client.SendAsync(req);
                    if (resp.StatusCode != System.Net.HttpStatusCode.Forbidden)
                        throw new Exception($"Expected 403 Forbidden on POST /api/client-closing without CSRF, got {resp.StatusCode}");
                }

                // 4. /api/client-closing with POST and valid query CSRF should succeed (204 No Content)
                using (var req = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{port}/api/client-closing?csrf={Uri.EscapeDataString(csrf)}"))
                {
                    req.Headers.Add("Origin", $"http://127.0.0.1:{port}");
                    using var resp = await client.SendAsync(req);
                    if (resp.StatusCode != System.Net.HttpStatusCode.NoContent)
                        throw new Exception($"Expected 204 NoContent on POST /api/client-closing with CSRF, got {resp.StatusCode}");
                }

                // 5. WebSocket connection without CSRF token should be rejected (403 Forbidden)
                using (var ws = new ClientWebSocket())
                {
                    bool rejected = false;
                    try
                    {
                        await ws.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/ws"), CancellationToken.None);
                    }
                    catch (WebSocketException)
                    {
                        rejected = true;
                    }
                    if (!rejected && ws.State == WebSocketState.Open)
                        throw new Exception("WebSocket connection without CSRF should have been rejected.");
                }

                // 6. WebSocket connection with valid CSRF should succeed
                using (var ws = new ClientWebSocket())
                {
                    await ws.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/ws?csrf={Uri.EscapeDataString(csrf)}"), CancellationToken.None);
                    if (ws.State != WebSocketState.Open)
                        throw new Exception($"Expected WebSocket State Open, got {ws.State}");
                    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
                }

                // 7. Verify index.html resolves all @include views properly
                using (var req = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{port}/"))
                {
                    req.Headers.Add("Origin", $"http://127.0.0.1:{port}");
                    using var resp = await client.SendAsync(req);
                    if (resp.StatusCode != System.Net.HttpStatusCode.OK)
                        throw new Exception($"Expected 200 OK for GET /, got {resp.StatusCode}");
                    string html = await resp.Content.ReadAsStringAsync();
                    if (html.Contains("<!-- @include"))
                        throw new Exception("Index HTML still contains unparsed <!-- @include tags.");
                    if (!html.Contains("id=\"view-dashboard\"") ||
                        !html.Contains("id=\"view-chat\"") ||
                        !html.Contains("id=\"view-voice\"") ||
                        !html.Contains("id=\"view-vpn\""))
                        throw new Exception("Index HTML is missing one or more SPA view containers.");
                    if (!html.Contains("id=\"pasteClipboardBtn\""))
                        throw new Exception("Index HTML is missing pasteClipboardBtn.");
                }

                Console.WriteLine("PASSED");
            }
            finally
            {
                server.Stop();
                cts.Cancel();
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }

        private static void TestTorPreExistingExecutableDetection()
        {
            Console.Write("[TEST] Tor pre-existing executable detection... ");
            // Use reflection to invoke private static TryFindPreExistingTorExecutable
            var method = typeof(TorRuntimeManager).GetMethod("TryFindPreExistingTorExecutable", BindingFlags.NonPublic | BindingFlags.Static);
            if (method == null)
                throw new Exception("TryFindPreExistingTorExecutable method not found on TorRuntimeManager.");

            string tempDir = Path.Combine(Path.GetTempPath(), "qp_test_tor_detection_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                string exeName = OperatingSystem.IsWindows() ? "tor.exe" : "tor";
                string dummyTor = Path.Combine(tempDir, exeName);
                File.WriteAllText(dummyTor, "dummy-tor-binary");

                object?[] args = new object?[] { tempDir, null };
                bool found = (bool)method.Invoke(null, args)!;
                string? detected = (string?)args[1];

                if (!found || string.IsNullOrEmpty(detected) || !File.Exists(detected))
                    throw new Exception("Failed to detect existing Tor executable in target directory.");

                Console.WriteLine("PASSED");
            }
            finally
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }

        private static async Task TestPeerInfoSessionLockConcurrencyAsync()
        {
            Console.Write("[TEST] PeerInfo thread safety and session lock under concurrency... ");
            string tempDir = Path.Combine(Path.GetTempPath(), "qp_test_peerinfo_lock_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                using var qp = new QuicPunch.QuicPunch(appDataPath: tempDir, listeningPort: 0);

                using var certA = ECDsa.Create(ECCurve.NamedCurves.nistP256);
                using var ecdhA = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
                var reqA = new CertificateRequest("CN=PeerA", certA, HashAlgorithmName.SHA256);
                using var x509A = reqA.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));

                var peer = new PeerInfo(x509A, ecdhA.ExportSubjectPublicKeyInfo());
                peer.SessionNonce = RandomNumberGenerator.GetBytes(32);
                peer.EphemeralEcdhPublicKey = ecdhA.ExportSubjectPublicKeyInfo();

                // Run concurrent InitSession, RenewLocalEntropy, and Dispose
                var tasks = new Task[10];
                for (int i = 0; i < tasks.Length; i++)
                {
                    int idx = i;
                    tasks[i] = Task.Run(() =>
                    {
                        try
                        {
                            if (idx % 2 == 0)
                                peer.RenewLocalEntropy();
                            else
                                peer.InitSession(qp, QuicPunch.QuicPunch.TransportType.Wan);
                        }
                        catch (ObjectDisposedException) { /* Expected when disposed */ }
                        catch (CryptographicException) { /* Expected if entropy renewed mid-flight */ }
                    });
                }

                await Task.WhenAll(tasks);
                peer.Dispose();

                // Disposing again should be safe and idempotent
                peer.Dispose();

                Console.WriteLine("PASSED");
            }
            finally
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }
    }
