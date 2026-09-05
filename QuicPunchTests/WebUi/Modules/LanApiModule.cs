using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;
using QuicPunch.Helpers;
using QuicPunchTests.Protocols;
using QuicPunchTests.Settings;

namespace QuicPunchTests.WebUi.Modules;

internal sealed class LanApiModule
{
    private readonly QuicPunch.QuicPunch _qcc;
    private readonly VirtualLanHandler _lanHandler;
    private readonly AppPreferencesStore _preferences;

    public LanApiModule(QuicPunch.QuicPunch qcc, VirtualLanHandler lanHandler, AppPreferencesStore preferences)
    {
        _qcc = qcc;
        _lanHandler = lanHandler;
        _preferences = preferences;
    }

    [DllImport("libc", EntryPoint = "geteuid", SetLastError = true)]
    private static extern uint geteuid();

    public static bool IsRunningAsAdmin()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                using var id = WindowsIdentity.GetCurrent();
                return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
            }
            if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            {
                return Environment.UserName == "root" || geteuid() == 0;
            }
        }
        catch { }
        return false;
    }

    public static bool RestartWithAdminPrivileges()
    {
        try
        {
            string exe = Environment.ProcessPath ?? Environment.GetCommandLineArgs()[0];
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                UseShellExecute = true,
                WorkingDirectory = Environment.CurrentDirectory
            };

            if (OperatingSystem.IsWindows())
            {
                psi.Verb = "runas"; // Triggers native Windows UAC prompt
            }
            else if (OperatingSystem.IsLinux())
            {
                if (File.Exists("/usr/bin/pkexec"))
                {
                    psi.FileName = "/usr/bin/pkexec";
                    psi.Arguments = $"\"{exe}\"";
                }
                else
                {
                    psi.FileName = "sudo";
                    psi.Arguments = $"\"{exe}\"";
                }
            }
            else if (OperatingSystem.IsMacOS())
            {
                psi.FileName = "/usr/bin/osascript";
                psi.Arguments = $"-e 'do shell script \"\\\"{exe}\\\"\" with administrator privileges'";
            }

            WebUiServer.LogEvent("[SYSTEM] Relaunching with administrative privileges...");
            Process.Start(psi);

            _ = Task.Run(async () =>
            {
                await Task.Delay(1200);
                Environment.Exit(0);
            });
            return true;
        }
        catch (Exception ex)
        {
            WebUiServer.LogEvent($"[SYSTEM] Failed to relaunch as administrator: {ex.Message}");
            return false;
        }
    }

    public async Task<bool> HandleRequestAsync(string path, HttpListenerRequest req, HttpListenerResponse resp)
    {
        if (path == "/api/restart-as-admin" && req.HttpMethod == "POST")
        {
            bool success = RestartWithAdminPrivileges();
            await WebUiContext.WriteJsonAsync(resp, new { success }).ConfigureAwait(false);
            return true;
        }

        if (path == "/api/lan-settings" && req.HttpMethod == "POST")
        {
            using JsonDocument doc = await WebUiContext.ReadJsonAsync(req, path).ConfigureAwait(false);
            AppPreferences current = _preferences.Snapshot();
            bool enabled = doc.RootElement.TryGetProperty("enabled", out var enabledEl) ? enabledEl.GetBoolean() : current.LanEnabled;
            bool autoAssign = doc.RootElement.TryGetProperty("autoAssign", out var autoEl) ? autoEl.GetBoolean() : current.LanAutoAssign;
            string ip = doc.RootElement.TryGetProperty("ip", out var ipEl) ? ipEl.GetString() ?? current.LanIp : current.LanIp;
            string mask = doc.RootElement.TryGetProperty("subnetMask", out var maskEl) ? maskEl.GetString() ?? current.LanSubnetMask : current.LanSubnetMask;
            int mtu = doc.RootElement.TryGetProperty("mtu", out var mtuEl) ? mtuEl.GetInt32() : current.LanMtu;
            if (!_lanHandler.SetMtu(mtu)) throw new InvalidDataException("MTU must be between 1200 and 9000.");

            if (autoAssign)
                _lanHandler.ConfigureAutoAssignment(_qcc.CertManager.CertPublicHash, _qcc.PoolId);
            else if (!_lanHandler.ConfigureManualAddress(ip, mask))
                throw new InvalidDataException("A valid IPv4 address and subnet mask are required.");

            if (enabled)
            {
                _lanHandler.SetupTun();
                if (_lanHandler.IsActive)
                    _qcc.RegisterProtocol(_lanHandler);
                else
                    _qcc.RemoveProtocol(_lanHandler);
            }
            else
            {
                _qcc.RemoveProtocol(_lanHandler);
                _lanHandler.StopTun();
            }

            string appliedIp = _lanHandler.LocalIp.ToString();
            _preferences.Update(p =>
            {
                p.LanEnabled = enabled;
                p.LanAutoAssign = autoAssign;
                p.LanIp = autoAssign ? appliedIp : ip;
                p.LanSubnetMask = autoAssign ? "255.255.255.0" : mask;
                p.LanMtu = mtu;
            });
            await WebUiContext.WriteJsonAsync(resp, new { success = true, enabled, autoAssign, ip = appliedIp, subnetMask = _lanHandler.SubnetMask, mtu }).ConfigureAwait(false);
            return true;
        }

        if ((path == "/api/lan-config" || path == "/api/lan-configure-ip") && req.HttpMethod == "POST")
        {
            using JsonDocument doc = await WebUiContext.ReadJsonAsync(req, path).ConfigureAwait(false);
            string ip = doc.RootElement.GetProperty("ip").GetString() ?? "";
            string mask = doc.RootElement.TryGetProperty("subnetMask", out var maskEl) ? maskEl.GetString() ?? "255.255.255.0" : "255.255.255.0";
            if (!_lanHandler.SetVirtualIp(ip, mask)) throw new InvalidDataException("Invalid IPv4 address.");
            _preferences.Update(p => { p.LanAutoAssign = false; p.LanIp = ip; p.LanSubnetMask = mask; });
            await WebUiContext.WriteJsonAsync(resp, new { success = true, ip }).ConfigureAwait(false);
            return true;
        }

        return false;
    }
}
