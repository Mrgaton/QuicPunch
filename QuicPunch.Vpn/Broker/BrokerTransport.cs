using System.ComponentModel;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace QuicPunch.Vpn.Broker;

internal static class BrokerTransport
{
    [DllImport("libc")] internal static extern uint getuid();
    [DllImport("libc", SetLastError = true)] private static extern int chown(string path, uint owner, uint group);
    [DllImport("libc", SetLastError = true)] private static extern int getsockopt(int fd, int level, int option, out Credentials value, ref uint length);
    [StructLayout(LayoutKind.Sequential)] private struct Credentials { public int Pid; public uint Uid; public uint Gid; }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint pid);
    internal static string CurrentOwner
    {
        get { if (!OperatingSystem.IsWindows()) return getuid().ToString(); using var identity = WindowsIdentity.GetCurrent(); return identity.User!.Value; }
    }
    internal static string PipeName(string owner) => "QuicNet.Vpn." + owner;
    internal static string SocketPath(string owner) => "/run/quicnet/vpn-" + owner + ".sock";
    internal static async Task<Stream> ConnectAsync(CancellationToken ct)
    {
        if (OperatingSystem.IsWindows())
        {
            var pipe = new NamedPipeClientStream(".", PipeName(CurrentOwner), PipeDirection.InOut, PipeOptions.Asynchronous,
                TokenImpersonationLevel.Identification);
            try
            {
                await pipe.ConnectAsync(1500, ct);
                // The pipe ACL authenticates clients. SCM authenticates the server without requiring access to a SYSTEM process token.
                if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out uint pid)) throw new Win32Exception(Marshal.GetLastWin32Error());
                if (pid == 0 || pid != WindowsBrokerService.RunningProcessId)
                    throw new UnauthorizedAccessException("The pipe does not belong to the installed VPN broker service.");
                return pipe;
            }
            catch { pipe.Dispose(); throw; }
        }
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(SocketPath(CurrentOwner)), ct);
            RequireUid(socket, 0);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch { socket.Dispose(); throw; }
    }
    private static void RequireUid(Socket socket, uint expected)
    {
        uint size = 12;
        if (getsockopt(socket.Handle.ToInt32(), 1, 17, out var credentials, ref size) != 0 || credentials.Uid != expected)
            throw new UnauthorizedAccessException("Unexpected VPN broker socket identity.");
    }
    internal static async Task ServeAsync(VpnBrokerConfiguration config, Func<Stream, Task> session, CancellationToken ct)
    {
        if (OperatingSystem.IsWindows())
        {
            var owner = new SecurityIdentifier(config.Owner);
            var security = new PipeSecurity();
            security.SetAccessRuleProtection(true, false);
            security.AddAccessRule(new(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
            security.AddAccessRule(new(owner, PipeAccessRights.ReadWrite, AccessControlType.Allow));
            while (!ct.IsCancellationRequested)
            {
                using var pipe = NamedPipeServerStreamAcl.Create(PipeName(config.Owner), PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance, 65536, 65536, security);
                await pipe.WaitForConnectionAsync(ct);
                bool authorized = false;
                pipe.RunAsClient(() => { if (OperatingSystem.IsWindows()) { using var identity = WindowsIdentity.GetCurrent(true); authorized = identity?.User == owner; } });
                if (authorized) await session(pipe);
            }
            return;
        }
        uint uid = uint.Parse(config.Owner);
        Directory.CreateDirectory("/run/quicnet");
        File.SetUnixFileMode("/run/quicnet", UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        string path = SocketPath(config.Owner);
        if (File.Exists(path)) File.Delete(path);
        using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(path));
        if (chown(path, uid, uint.MaxValue) != 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        listener.Listen(1);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                using var client = await listener.AcceptAsync(ct);
                // A rejected local connection must not stop the service for its authorized owner.
                try { RequireUid(client, uid); }
                catch (UnauthorizedAccessException) { continue; }
                using var stream = new NetworkStream(client, ownsSocket: false);
                await session(stream);
            }
        }
        finally { File.Delete(path); }
    }
}
