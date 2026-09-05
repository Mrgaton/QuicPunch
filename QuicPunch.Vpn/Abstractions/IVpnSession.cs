using System;
using System.Threading;
using System.Threading.Tasks;

namespace QuicPunch.Vpn;

/// <summary>
/// Represents an active Layer-3 virtual network packet I/O session.
/// Enables reading and writing raw IP packets directly to/from the host OS network stack.
/// </summary>
public interface IVpnSession : IDisposable, IAsyncDisposable
{
    /// <summary>
    /// Waits until one or more IP packets are available to read, or until the timeout expires or cancellation is requested.
    /// </summary>
    /// <param name="timeoutMilliseconds">Maximum time to wait in milliseconds.</param>
    /// <param name="cancellationToken">Cancellation token to cancel waiting early.</param>
    /// <returns>True if packets are available to read; otherwise false.</returns>
    bool WaitForPacket(int timeoutMilliseconds, CancellationToken cancellationToken = default);

    /// <summary>
    /// Receives the next available Layer-3 IP packet from the OS network stack into the provided destination buffer.
    /// </summary>
    /// <param name="destination">Buffer to receive the packet.</param>
    /// <returns>The number of bytes read into the destination buffer, or 0 if no packets are available.</returns>
    int ReceivePacket(Span<byte> destination);

    /// <summary>
    /// Injects a Layer-3 IP packet received from the peer network into the host OS network stack.
    /// </summary>
    /// <param name="packet">Raw IPv4/IPv6 packet to send.</param>
    void SendPacket(ReadOnlySpan<byte> packet);

    /// <summary>
    /// Closes the session and releases resources.
    /// </summary>
    void Close();
}
