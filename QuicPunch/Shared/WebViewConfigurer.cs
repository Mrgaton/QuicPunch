using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using Tetrio;

public static class WebViewConfigurer
{
    private static Dictionary<string, string> ExtensionRepos = new()
    {
        {"gorhill/uBlock", " uBlock0_.chromium.zip" },
    };

    private static Form RootForm = null;
    private static WebView2 RootWv = null;
    public static async Task ConfigureWebView(WebView2 wv, Form rootForm)
    {
        RootForm = rootForm;
        RootWv = wv;

        CoreWebView2EnvironmentOptions options = new CoreWebView2EnvironmentOptions()
        {
            AreBrowserExtensionsEnabled = true,
        };

        string tempWebCacheDir = Path.Combine(Path.GetTempPath(), Application.ProductName);

        CoreWebView2Environment webView2Environment = await CoreWebView2Environment.CreateAsync(null, tempWebCacheDir, options);

        await wv.EnsureCoreWebView2Async(webView2Environment);

#if !DEBUG
        wv.CoreWebView2.Settings.AreDevToolsEnabled = false;
#endif

        foreach (var ext in ExtensionRepos)
        {
            var attachement = await GithubReleases.GetAttachement(ext.Key, ext.Value);
            var extPath = await DownloadAndExtractExtensionAsync(attachement);
            await wv.CoreWebView2.Profile.AddBrowserExtensionAsync(extPath);
        }

        await wv.CoreWebView2.Profile.GetBrowserExtensionsAsync();
        await wv.CoreWebView2.Profile.ClearBrowsingDataAsync(CoreWebView2BrowsingDataKinds.CacheStorage);
        wv.CoreWebView2.NavigationCompleted += CoreWebView2_NavigationCompleted;

        wv.KeyDown += KeyDown;
    }
    private static void KeyDown(object sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.F11)
        {
            if (RootForm.FormBorderStyle == FormBorderStyle.None)
            {
                RootForm.FormBorderStyle = FormBorderStyle.Sizable;
                RootForm.WindowState = FormWindowState.Normal;
            }
            else
            {
                RootForm.FormBorderStyle = FormBorderStyle.None;
                RootForm.WindowState = FormWindowState.Maximized;
            }
        }
        else if (e.KeyCode == Keys.F5)
        {
            RootWv.Refresh();
        }
    }

    private static bool Optimized = false;
    private static void CoreWebView2_NavigationCompleted(object sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        //this.webView.ExecuteScriptAsync(@"");

        if (!Optimized)
        {
            Optimized = true;

            Process.GetProcessesByName("msedgewebview2").ToList().ForEach(p => p.PriorityClass = ProcessPriorityClass.High);
        }
    }
    private static MD5 HashAlg = MD5.Create();
    public static async Task<string> DownloadAndExtractExtensionAsync(string extensionZipUrl)
    {
        string tempPath = Path.GetTempPath();

        string extractFolder = Path.Combine(tempPath, "ext_" + BitConverter.ToString(HashAlg.ComputeHash(Encoding.UTF8.GetBytes(extensionZipUrl))).Replace("-", ""));

        if (!Directory.Exists(extractFolder))
        {
            Directory.CreateDirectory(extractFolder);

            using (HttpClient client = new HttpClient())
            {
                using (ZipArchive archive = new ZipArchive(await client.GetStreamAsync(extensionZipUrl)))
                {
                    archive.ExtractToDirectory(extractFolder);
                }
            }
        }

        string[] directories = Directory.GetDirectories(extractFolder);

        if (directories.Length > 0)
        {
            return directories[0];
        }
        else
        {
            return extractFolder;
        }
    }
}
