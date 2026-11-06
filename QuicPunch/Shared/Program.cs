using System.Buffers.Text;

namespace SharedCodeTests
{
    internal class Program
    {
        public static void Main(string[] args)
        {
            HttpClient client = CertificateTransparencyLogAndDnsOverHttpHandlerClass.CTDohClient;

            CertificateTransparencyLog.InitializeAsync().GetAwaiter().GetResult();

            for (int i = 0; i < 2; i++)
            {
                Console.WriteLine(DateTime.Now);
                Thread.Sleep(1000);
            }

            CertificateTransparencyLogAndDnsOverHttpHandlerClass.SetNetworkTimeAsync().GetAwaiter().GetResult();

            for (int i = 0; i < 5; i++)
            {
                Console.WriteLine(DateTime.Now);
                Thread.Sleep(1000);
            }

            RunPositiveTest(client, "https://www.google.com");
            RunNegativeTest(client, "https://wrong.host.badssl.com/");
        }

        private static void RunPositiveTest(HttpClient client, string url)
        {
            var response = client.GetAsync(url).GetAwaiter().GetResult();
            Console.WriteLine($"{url} validation: {(response.IsSuccessStatusCode ? "PASS" : "FAIL")} ({(int)response.StatusCode})");
        }

        private static void RunNegativeTest(HttpClient client, string url)
        {
            try
            {
                client.GetAsync(url).GetAwaiter().GetResult();
            }
            catch (HttpRequestException)
            {
                Console.WriteLine($"{url} validation: EXPECTED FAIL (error triggered)");
                return;
            }

            throw new InvalidOperationException($"Negative validation test failed. Expected TLS validation error for '{url}'.");
        }
    }
}