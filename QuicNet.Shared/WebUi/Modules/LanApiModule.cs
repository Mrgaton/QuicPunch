using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;
using QuicPunch.Helpers;
using QuicNet.Protocols;
using QuicNet.Settings;
using QuicNet.Vpn;

namespace QuicNet.WebUi.Modules;

internal sealed class LanApiModule
{
    private readonly QuicPunch.QuicPunch _qcc;
    private readonly IVirtualLanHandler? _lanHandler;
    private readonly AppPreferencesStore _preferences;

    public LanApiModule(QuicPunch.QuicPunch qcc, IVirtualLanHandler? lanHandler, AppPreferencesStore preferences)
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
            if (OperatingSystem.IsLinux())
            {
                return Environment.UserName == "root" || geteuid() == 0;
            }
        }
        catch { }
        return false;
    }

    private static int _isRestarting = 0;

    private static string BashQuote(string value) => "'" + value.Replace("'", "'\\''") + "'";

    private static string? ResolveExecutable(string fileName)
    {
        if (File.Exists(fileName)) return Path.GetFullPath(fileName);
        string? pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrEmpty(pathEnv))
        {
            foreach (string dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                try
                {
                    string candidate = Path.Combine(dir, fileName);
                    if (File.Exists(candidate)) return candidate;
                }
                catch { }
            }
        }
        return null;
    }

    public static async Task<bool> RestartWithAdminPrivilegesAsync()
    {
        if (Interlocked.CompareExchange(ref _isRestarting, 1, 0) != 0)
        {
            WebUiServer.LogEvent("[SYSTEM] Elevation restart already in progress.");
            return false;
        }

        try
        {
            string[] rawArgs = Environment.GetCommandLineArgs();
            string exe = Environment.ProcessPath ?? (rawArgs.Length > 0 ? rawArgs[0] : "");
            var remainingArgs = rawArgs.Length > 1 ? rawArgs.Skip(1).ToArray() : Array.Empty<string>();

            // Detect if running via dotnet host (e.g. dotnet run or dotnet QuicPunchTests.dll)
            bool isDotnetHost = Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase);
            string targetCommand;
            List<string> commandArgs = new();

            if (isDotnetHost)
            {
                targetCommand = exe;
                string entryDll = System.Reflection.Assembly.GetEntryAssembly()?.Location ?? (rawArgs.Length > 0 ? rawArgs[0] : "");
                if (!string.IsNullOrEmpty(entryDll) && File.Exists(entryDll))
                {
                    commandArgs.Add(entryDll);
                }
                commandArgs.AddRange(remainingArgs);
            }
            else
            {
                targetCommand = exe;
                commandArgs.AddRange(remainingArgs);
            }

            if (!Path.IsPathRooted(targetCommand) && !File.Exists(targetCommand))
            {
                string? resolved = ResolveExecutable(targetCommand);
                if (!string.IsNullOrEmpty(resolved)) targetCommand = resolved;
            }

            if (OperatingSystem.IsWindows())
            {
                var psi = new ProcessStartInfo
                {
                    FileName = targetCommand,
                    WorkingDirectory = Environment.CurrentDirectory,
                    UseShellExecute = true,
                    Verb = "runas" // Native Windows UAC elevation
                };
                foreach (string arg in commandArgs)
                {
                    psi.ArgumentList.Add(arg);
                }

                WebUiServer.LogEvent("[SYSTEM] Requesting administrator elevation (UAC)...");
                try
                {
                    var proc = Process.Start(psi);
                    if (proc == null)
                    {
                        WebUiServer.LogEvent("[SYSTEM] Process.Start returned null for UAC elevation.");
                        Interlocked.Exchange(ref _isRestarting, 0);
                        return false;
                    }
                    return true;
                }
                catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) // ERROR_CANCELLED
                {
                    WebUiServer.LogEvent("[SYSTEM] UAC elevation was cancelled by user.");
                    Interlocked.Exchange(ref _isRestarting, 0);
                    return false;
                }
            }
            else if (OperatingSystem.IsLinux())
            {
                string tempDir = Path.GetTempPath();
                string scriptId = Guid.NewGuid().ToString("N");
                string scriptPath = Path.Combine(tempDir, $"qp_elevate_{scriptId}.sh");
                string readyFile = Path.Combine(tempDir, $"qp_elevate_{scriptId}.ready");
                int parentPid = Environment.ProcessId;

                string display = Environment.GetEnvironmentVariable("DISPLAY") ?? "";
                string waylandDisplay = Environment.GetEnvironmentVariable("WAYLAND_DISPLAY") ?? "";
                string xdgRuntime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") ?? "";
                string xauth = Environment.GetEnvironmentVariable("XAUTHORITY") ?? "";
                string dbus = Environment.GetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS") ?? "";
                string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                string xdgConfig = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") ?? (string.IsNullOrEmpty(home) ? "" : Path.Combine(home, ".config"));
                string path = Environment.GetEnvironmentVariable("PATH") ?? "";
                string dotnetRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT") ?? "";

                if (string.IsNullOrEmpty(xauth) && !string.IsNullOrEmpty(home) && File.Exists(Path.Combine(home, ".Xauthority")))
                {
                    xauth = Path.Combine(home, ".Xauthority");
                }

                string argsArrayCode = "";
                string argsExpansion = "";
                if (commandArgs.Count > 0)
                {
                    argsArrayCode = "CMD_ARGS=(\n" + string.Join("\n", commandArgs.Select(a => "    " + BashQuote(a))) + "\n)";
                    argsExpansion = "\"${CMD_ARGS[@]}\"";
                }

                string scriptContent = $@"#!/bin/bash
set -e

# Allow user access to any newly written files or directories in user home
umask 0000

# Propagate desktop, display and user session environment
[ -n {BashQuote(display)} ] && export DISPLAY={BashQuote(display)}
[ -n {BashQuote(waylandDisplay)} ] && export WAYLAND_DISPLAY={BashQuote(waylandDisplay)}
[ -n {BashQuote(xdgRuntime)} ] && export XDG_RUNTIME_DIR={BashQuote(xdgRuntime)}
[ -n {BashQuote(xauth)} ] && export XAUTHORITY={BashQuote(xauth)}
[ -n {BashQuote(dbus)} ] && export DBUS_SESSION_BUS_ADDRESS={BashQuote(dbus)}
[ -n {BashQuote(home)} ] && export HOME={BashQuote(home)}
[ -n {BashQuote(xdgConfig)} ] && export XDG_CONFIG_HOME={BashQuote(xdgConfig)}
[ -n {BashQuote(path)} ] && export PATH={BashQuote(path)}
[ -n {BashQuote(dotnetRoot)} ] && export DOTNET_ROOT={BashQuote(dotnetRoot)}

# WebKitGTK / Photino sandbox allowance under root
export WEBKIT_DISABLE_SANDBOX_THIS_IS_DANGEROUS=1

{argsArrayCode}

# Signal parent that root authentication succeeded
touch {BashQuote(readyFile)}

# Wait for parent process to terminate and release port 5000 and sockets (up to 5s)
for i in $(seq 1 50); do
    if ! kill -0 {parentPid} 2>/dev/null; then
        break
    fi
    sleep 0.1
done

# Clean up launcher files
rm -f {BashQuote(readyFile)}
rm -f {BashQuote(scriptPath)}

# Change directory and launch elevated application
cd {BashQuote(Environment.CurrentDirectory)}
exec {BashQuote(targetCommand)} {argsExpansion} </dev/null
";

                await File.WriteAllTextAsync(scriptPath, scriptContent).ConfigureAwait(false);
                try
                {
                    File.SetUnixFileMode(scriptPath,
                        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                        UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                        UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
                }
                catch { }

                var psi = new ProcessStartInfo
                {
                    WorkingDirectory = Environment.CurrentDirectory,
                    UseShellExecute = false
                };

                // 1. Try pkexec (GUI polkit prompt)
                if (File.Exists("/usr/bin/pkexec"))
                {
                    psi.FileName = "/usr/bin/pkexec";
                    psi.ArgumentList.Add("--keep-cwd");
                    psi.ArgumentList.Add("/bin/bash");
                    psi.ArgumentList.Add(scriptPath);
                }
                else
                {
                    // 2. Try launching inside an available graphical terminal emulator with sudo
                    string[] knownTerminals = { "/usr/bin/alacritty", "/usr/bin/foot", "/usr/bin/x-terminal-emulator", "/usr/bin/gnome-terminal", "/usr/bin/xterm" };
                    string? terminal = knownTerminals.FirstOrDefault(File.Exists);

                    if (terminal != null)
                    {
                        psi.FileName = terminal;
                        if (terminal.EndsWith("foot"))
                        {
                            psi.ArgumentList.Add("sudo");
                            psi.ArgumentList.Add("/bin/bash");
                            psi.ArgumentList.Add(scriptPath);
                        }
                        else
                        {
                            psi.ArgumentList.Add("-e");
                            psi.ArgumentList.Add("sudo");
                            psi.ArgumentList.Add("/bin/bash");
                            psi.ArgumentList.Add(scriptPath);
                        }
                    }
                    else
                    {
                        // 3. Fallback directly to sudo
                        psi.FileName = "sudo";
                        psi.ArgumentList.Add("/bin/bash");
                        psi.ArgumentList.Add(scriptPath);
                    }
                }

                WebUiServer.LogEvent("[SYSTEM] Requesting administrator authorization...");
                Process? startedProcess;
                try
                {
                    startedProcess = Process.Start(psi);
                }
                catch (Exception ex)
                {
                    WebUiServer.LogEvent($"[SYSTEM] Failed to start elevation process: {ex.Message}");
                    try { File.Delete(scriptPath); } catch { }
                    Interlocked.Exchange(ref _isRestarting, 0);
                    return false;
                }

                if (startedProcess == null)
                {
                    WebUiServer.LogEvent("[SYSTEM] Process.Start returned null for elevation.");
                    try { File.Delete(scriptPath); } catch { }
                    Interlocked.Exchange(ref _isRestarting, 0);
                    return false;
                }

                // Wait for the user to complete the authentication prompt
                var sw = Stopwatch.StartNew();
                bool authenticated = false;
                while (sw.Elapsed < TimeSpan.FromSeconds(120))
                {
                    if (File.Exists(readyFile))
                    {
                        authenticated = true;
                        break;
                    }

                    if (startedProcess.HasExited)
                    {
                        await Task.Delay(100).ConfigureAwait(false);
                        if (File.Exists(readyFile))
                        {
                            authenticated = true;
                        }
                        break;
                    }

                    await Task.Delay(150).ConfigureAwait(false);
                }

                if (!authenticated)
                {
                    WebUiServer.LogEvent("[SYSTEM] Elevation cancelled or authorization failed.");
                    try { File.Delete(scriptPath); } catch { }
                    try { File.Delete(readyFile); } catch { }
                    Interlocked.Exchange(ref _isRestarting, 0);
                    return false;
                }

                WebUiServer.LogEvent("[SYSTEM] Elevation authorized. Relaunching application...");
                return true;
            }
            else
            {
                var psi = new ProcessStartInfo
                {
                    FileName = targetCommand,
                    WorkingDirectory = Environment.CurrentDirectory,
                    UseShellExecute = true
                };
                foreach (string arg in commandArgs)
                {
                    psi.ArgumentList.Add(arg);
                }
                var proc = Process.Start(psi);
                if (proc == null)
                {
                    Interlocked.Exchange(ref _isRestarting, 0);
                    return false;
                }
                return true;
            }
        }
        catch (Exception ex)
        {
            WebUiServer.LogEvent($"[SYSTEM] Failed to relaunch as administrator: {ex.Message}");
            Interlocked.Exchange(ref _isRestarting, 0);
            return false;
        }
    }

    public async Task<bool> HandleRequestAsync(string path, HttpListenerRequest req, HttpListenerResponse resp)
    {
        if (path == "/api/vpn/config" && _lanHandler?.Control is { } control)
        {
            if (req.HttpMethod == "POST")
            {
                using var body = await WebUiContext.ReadJsonAsync(req, path).ConfigureAwait(false);
                var config = global::QuicNet.WebUi.WebUiContext.ReadJsonValue<VpnConfiguration>(body.RootElement.GetProperty("configuration"), VpnConfigurationStore.JsonOptions)
                    ?? throw new InvalidDataException("Configuration is required.");
                control.SaveConfiguration(config, body.RootElement.GetProperty("expectedRevision").GetInt64());
                _preferences.Update(p => p.LanEnabled = config.Enabled);
                WebUiServer.BroadcastStatusUpdate();
            }
            else if (req.HttpMethod != "GET") return false;
            await WebUiContext.WriteJsonAsync(resp, new global::QuicNet.Serialization.LanApiModuleMessage1(true, JsonSerializer.SerializeToElement(control.Configuration, global::QuicNet.WebUi.WebUiContext.JsonOptionsFor(VpnConfigurationStore.JsonOptions)), control.ConfigurationPath, control.ConfigurationError, control.OrchestrationStatus, control.PeerDefaults)).ConfigureAwait(false);
            return true;
        }
        if (path == "/api/restart-as-admin" && req.HttpMethod == "POST")
        {
            bool success = await RestartWithAdminPrivilegesAsync().ConfigureAwait(false);
            await WebUiContext.WriteJsonAsync(resp, new global::QuicNet.Serialization.LanApiModuleMessage2(success)).ConfigureAwait(false);
            if (success)
            {
                _ = Task.Run(async () =>
                {
                    await Task.Delay(500).ConfigureAwait(false);
                    WebUiServer.Instance?.Stop();
                    Environment.Exit(0);
                });
            }
            return true;
        }

        if (path.StartsWith("/api/lan", StringComparison.Ordinal))
        {
            if (_lanHandler == null)
            {
                await WebUiContext.WriteJsonAsync(resp, new global::QuicNet.Serialization.LanApiModuleMessage3(false, "Puente LAN fuera de alcance en esta plataforma."), 400).ConfigureAwait(false);
                return true;
            }
        }

        if (_lanHandler == null) return false;

        if (path == "/api/lan-toggle" && req.HttpMethod == "POST")
        {
            using JsonDocument doc = await WebUiContext.ReadJsonAsync(req, path, allowEmpty: true).ConfigureAwait(false);
            bool targetEnabled;
            if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("enabled", out var enEl))
            {
                targetEnabled = enEl.GetBoolean();
            }
            else
            {
                targetEnabled = !(_lanHandler.Control?.Configuration.Enabled ?? _lanHandler.IsActive);
            }

            if (_lanHandler.Control is { } vpn)
            {
                var config = vpn.Configuration;
                config.Enabled = targetEnabled;
                vpn.SaveConfiguration(config, config.Revision);
                _preferences.Update(p => p.LanEnabled = targetEnabled);
                WebUiServer.BroadcastStatusUpdate();
                await WebUiContext.WriteJsonAsync(resp, new global::QuicNet.Serialization.LanApiModuleMessage4(true, targetEnabled, _lanHandler.IsActive, _lanHandler.LocalIp.ToString(), vpn.ConfigurationError)).ConfigureAwait(false);
                return true;
            }

            if (targetEnabled)
            {
                if (!IsRunningAsAdmin() && !_lanHandler.CanStartWithoutElevation)
                {
                    _lanHandler.NotifyPrivilegeRequired();
                    WebUiServer.BroadcastStatusUpdate();
                    await WebUiContext.WriteJsonAsync(resp, new global::QuicNet.Serialization.LanApiModuleMessage5(false, true, "Se requieren permisos de administrador en Windows (o root en Linux) para crear el adaptador virtual Wintun."), 403).ConfigureAwait(false);
                    return true;
                }

                try
                {
                    if (_lanHandler.AutoAssignEnabled)
                        _lanHandler.ConfigureAutoAssignment(_qcc.CertManager.CertPublicHash, _qcc.PoolId);

                    _lanHandler.SetupTun();
                    _qcc.RegisterProtocol(_lanHandler);
                    _preferences.Update(p => p.LanEnabled = true);
                    WebUiServer.LogEvent($"[LAN] Puente LAN activado en {_lanHandler.LocalIp}/{_lanHandler.SubnetMask} (MTU {_lanHandler.Mtu})");
                    WebUiServer.BroadcastStatusUpdate();

                    await WebUiContext.WriteJsonAsync(resp, new global::QuicNet.Serialization.LanApiModuleMessage6(true, _lanHandler.IsActive, _lanHandler.LocalIp.ToString(), _lanHandler.SubnetMask, _lanHandler.Mtu, _lanHandler.AdapterStatus)).ConfigureAwait(false);
                    return true;
                }
                catch (Exception ex)
                {
                    WebUiServer.LogEvent($"[LAN] Error al iniciar adaptador LAN: {ex.Message}");
                    await WebUiContext.WriteJsonAsync(resp, new global::QuicNet.Serialization.LanApiModuleMessage5(false, ex is Win32Exception w && (w.NativeErrorCode == 5 || w.NativeErrorCode == 1), ex.Message), 500).ConfigureAwait(false);
                    return true;
                }
            }
            else
            {
                _lanHandler.StopTun();
                _preferences.Update(p => p.LanEnabled = false);
                WebUiServer.LogEvent("[LAN] Puente LAN detenido.");
                WebUiServer.BroadcastStatusUpdate();
                await WebUiContext.WriteJsonAsync(resp, new global::QuicNet.Serialization.LanApiModuleMessage7(true, false)).ConfigureAwait(false);
                return true;
            }
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
            if (mtu <= 0 || mtu == 1500) mtu = IVirtualLanHandler.DefaultDatagramMtu;
            if (_lanHandler.Control is { } vpn)
            {
                var config = vpn.Configuration;
                config.Enabled = enabled; config.AutoAssign = autoAssign;
                config.LocalAddress = ip; config.SubnetMask = mask; config.Mtu = mtu;
                vpn.SaveConfiguration(config, config.Revision);
                _preferences.Update(p => { p.LanEnabled = enabled; p.LanAutoAssign = autoAssign; p.LanIp = ip; p.LanSubnetMask = mask; p.LanMtu = mtu; });
                WebUiServer.BroadcastStatusUpdate();
                await WebUiContext.WriteJsonAsync(resp, new global::QuicNet.Serialization.LanApiModuleMessage8(true, enabled, _lanHandler.IsActive, vpn.ConfigurationError)).ConfigureAwait(false);
                return true;
            }
            if (!_lanHandler.SetMtu(mtu)) throw new InvalidDataException("MTU must be between 1200 and 9000.");

            if (autoAssign)
                _lanHandler.ConfigureAutoAssignment(_qcc.CertManager.CertPublicHash, _qcc.PoolId);
            else if (!_lanHandler.ConfigureManualAddress(ip, mask))
                throw new InvalidDataException("A valid IPv4 address and subnet mask are required.");

            string? startError = null;
            if (enabled)
            {
                try
                {
                    _lanHandler.SetupTun();
                    _qcc.RegisterProtocol(_lanHandler);
                }
                catch (Exception ex)
                {
                    startError = ex.Message;
                }
            }
            else
            {
                _lanHandler.StopTun();
            }

            string appliedIp = _lanHandler.LocalIp.ToString();
            _preferences.Update(p =>
            {
                p.LanEnabled = enabled && _lanHandler.IsActive;
                p.LanAutoAssign = autoAssign;
                p.LanIp = autoAssign ? appliedIp : ip;
                p.LanSubnetMask = autoAssign ? _lanHandler.SubnetMask : mask;
                p.LanMtu = mtu;
            });
            WebUiServer.BroadcastStatusUpdate();

            if (!string.IsNullOrEmpty(startError))
            {
                await WebUiContext.WriteJsonAsync(resp, new global::QuicNet.Serialization.LanApiModuleMessage9(false, startError, !IsRunningAsAdmin(), false, false), 500).ConfigureAwait(false);
                return true;
            }

            await WebUiContext.WriteJsonAsync(resp, new global::QuicNet.Serialization.LanApiModuleMessage10(true, enabled, _lanHandler.IsActive, autoAssign, appliedIp, _lanHandler.SubnetMask, mtu, _lanHandler.AddressMode)).ConfigureAwait(false);
            return true;
        }

        if (path == "/api/lan-disconnect-peer" && req.HttpMethod == "POST")
        {
            using JsonDocument doc = await WebUiContext.ReadJsonAsync(req, path).ConfigureAwait(false);
            Guid peerId = WebUiContext.ParseGuid(doc.RootElement, "peerId");
            _lanHandler.Control?.PausePeer(peerId);
            bool disconnected = _lanHandler.DisconnectPeer(peerId);
            WebUiServer.BroadcastStatusUpdate();
            await WebUiContext.WriteJsonAsync(resp, new global::QuicNet.Serialization.LanApiModuleMessage11(true, disconnected)).ConfigureAwait(false);
            return true;
        }

        if (path == "/api/lan-ping-peer" && req.HttpMethod == "POST")
        {
            using JsonDocument doc = await WebUiContext.ReadJsonAsync(req, path).ConfigureAwait(false);
            string targetIpStr = doc.RootElement.GetProperty("ip").GetString()?.Trim() ?? "";
            if (!IPAddress.TryParse(targetIpStr, out var targetIp))
                throw new InvalidDataException("Invalid IPv4 address.");

            var (success, rttMs, error) = await _lanHandler.PingIpAsync(targetIp).ConfigureAwait(false);
            await WebUiContext.WriteJsonAsync(resp, new global::QuicNet.Serialization.LanApiModuleMessage12(success, rttMs, error)).ConfigureAwait(false);
            return true;
        }

        if ((path == "/api/lan-config" || path == "/api/lan-configure-ip") && req.HttpMethod == "POST")
        {
            using JsonDocument doc = await WebUiContext.ReadJsonAsync(req, path).ConfigureAwait(false);
            string ip = doc.RootElement.GetProperty("ip").GetString() ?? "";
            string mask = doc.RootElement.TryGetProperty("subnetMask", out var maskEl) ? maskEl.GetString() ?? "255.255.0.0" : "255.255.0.0";
            if (_lanHandler.Control is { } vpn)
            {
                var config = vpn.Configuration;
                config.AutoAssign = false; config.LocalAddress = ip; config.SubnetMask = mask;
                vpn.SaveConfiguration(config, config.Revision);
                WebUiServer.BroadcastStatusUpdate();
                await WebUiContext.WriteJsonAsync(resp, new global::QuicNet.Serialization.LanApiModuleMessage13(true, ip)).ConfigureAwait(false);
                return true;
            }
            if (!_lanHandler.SetVirtualIp(ip, mask)) throw new InvalidDataException("Invalid IPv4 address.");
            _preferences.Update(p => { p.LanAutoAssign = false; p.LanIp = ip; p.LanSubnetMask = mask; });
            WebUiServer.BroadcastStatusUpdate();
            await WebUiContext.WriteJsonAsync(resp, new global::QuicNet.Serialization.LanApiModuleMessage13(true, ip)).ConfigureAwait(false);
            return true;
        }

        return false;
    }
}
