using System;
using System.Runtime.InteropServices;

namespace QuicPunch.Vpn.Linux;

public static partial class LinuxTunApi
{
    public const int O_RDWR = 2;
    public const ulong TUNSETIFF = 0x400454CA;
    public const short IFF_TUN = 0x0001;
    public const short IFF_NO_PI = 0x1000;

    public const short POLLIN = 0x0001;
    public const short POLLERR = 0x0008;
    public const short POLLHUP = 0x0010;

    [LibraryImport("libc", EntryPoint = "open", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int Open(string path, int flags);

    [LibraryImport("libc", EntryPoint = "close", SetLastError = true)]
    public static partial int Close(int fd);

    [LibraryImport("libc", EntryPoint = "dup", SetLastError = true)]
    public static partial int Dup(int fd);

    [LibraryImport("libc", EntryPoint = "ioctl", SetLastError = true)]
    public static unsafe partial int Ioctl(int fd, ulong request, void* arg);

    [LibraryImport("libc", EntryPoint = "poll", SetLastError = true)]
    public static unsafe partial int Poll(PollFd* fds, nuint nfds, int timeout);

    [LibraryImport("libc", EntryPoint = "read", SetLastError = true)]
    public static unsafe partial nint Read(int fd, byte* buf, nuint count);

    [LibraryImport("libc", EntryPoint = "write", SetLastError = true)]
    public static unsafe partial nint Write(int fd, byte* buf, nuint count);
}

[StructLayout(LayoutKind.Explicit, Size = 40)]
public unsafe struct IfReq
{
    [FieldOffset(0)]
    public fixed byte IfName[16];

    [FieldOffset(16)]
    public short IfFlags;
}

[StructLayout(LayoutKind.Sequential)]
public struct PollFd
{
    public int Fd;
    public short Events;
    public short Revents;
}
