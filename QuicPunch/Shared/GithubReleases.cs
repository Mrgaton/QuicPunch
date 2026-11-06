using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;

namespace Tetrio
{
    public static class GithubReleases
    {
        private static HttpClient client = new HttpClient(new HttpClientHandler()
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.All
        })
        {
            DefaultRequestVersion = HttpVersion.Version20
        };
        public static async Task<string> GetAttachement(string userRepo, string tarjetFile)
        {
            using (HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Get, $"https://github.com/{userRepo}/releases/latest"))
            {
                using (HttpResponseMessage res = await client.SendAsync(req))
                {
                    string gitVersion = res.Headers.Location.ToString().Split('/').Last();

                    Version remoteVersion = ParseVersion(gitVersion);

#if DEBUG
                    Console.WriteLine(remoteVersion);
#endif

                    string[] attachements = await GetReleaseAssets(userRepo, gitVersion);

                    string[] attachementsNames = attachements.Select(a => (a.Split('/').Last())).ToArray();

                    string match = GetClosestName(tarjetFile, attachementsNames);

                    string attachement = attachements.First(l => l.EndsWith(match));

#if DEBUG
                    Console.WriteLine(attachement);
#endif

                    return attachement;
                }
            }
        }

        public static Version ParseVersion(string input)
        {
            short[] version = new short[4];
            short index = -1;
            string[] splited = input.Split('.');

            for (int i = 3; i >= 0; i--)
            {
                index++;

                if (i >= splited.Length) continue;

                version[index] = short.Parse(splited[(splited.Length - 1) - i]);
            }

            return Version.Parse(string.Join(".", version));
        }

        private static async Task<string[]> GetReleaseAssets(string userRepo, string version)
        {
            using (HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Get, $"https://github.com/{userRepo}/releases/expanded_assets/{version}"))
            {
                using (HttpResponseMessage res = await client.SendAsync(req))
                {
                    string assetsData = await res.Content.ReadAsStringAsync();

                    return assetsData.Split('\n')
                        .Select(l => l.TrimStart())
                        .Where(l => l.StartsWith("<a href=\"", StringComparison.InvariantCultureIgnoreCase))
                        .Select(l => "https://github.com" + l.Split('\"')[1]).ToArray();
                }
            }
        }
        public static string GetClosestName(string input, params string[][] namesArrays)
        {
            double minDistance = double.MinValue;
            string closestMatch = string.Empty;

            foreach (string[] names in namesArrays)
            {
                foreach (string name in names)
                {
                    double distance = (double)name.Length / ComputeLevenshteinDistance(input, name);

                    if (distance > minDistance)
                    {
                        minDistance = distance;
                        closestMatch = name;
                    }
                }
            }

            return closestMatch;
        }

        public static int ComputeLevenshteinDistance(string a, string b)
        {
            int[,] matrix = new int[a.Length + 1, b.Length + 1];
            for (int i = 0; i <= a.Length; i++) matrix[i, 0] = i;
            for (int j = 0; j <= b.Length; j++) matrix[0, j] = j;
            for (int i = 1; i <= a.Length; i++)
            {
                for (int j = 1; j <= b.Length; j++)
                {
                    int cost = (a[i - 1] == b[j - 1]) ? 0 : 1;

                    matrix[i, j] = Math.Min(Math.Min(matrix[i - 1, j] + 1, matrix[i, j - 1] + 1), matrix[i - 1, j - 1] + cost);
                }
            }

            return matrix[a.Length, b.Length];
        }
    }
}
