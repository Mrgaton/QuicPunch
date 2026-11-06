# File sharing and media transfer

The former RelayDrive handler is not part of the current application. Its old protocol GUID, limits and framing are not a supported contract.

The current entry points are:

- [Virtual disk](virtual-disk.md), with host-side folder grants, administrator exploration and a loopback WebDAV mount bridge.

- [Chat file sharing](../../QuicNet.Shared/WebUi/Modules/ChatApiModule.cs), carried by [ChatHandler](../../QuicNet.Shared/Protocols/ChatHandler.cs).
- [QuicWatch](../../QuicNet/Watch/README.md), with range-based transfer in [MediaTransferHandler](../../QuicNet.Shared/Watch/Protocols/MediaTransferHandler.cs) and caching in [MediaLibrary](../../QuicNet.Shared/Watch/Services/MediaLibrary.cs).

Both use application sessions over the peer multiplexer. See [application permissions](../core/automatic-peer-permissions.md) for authorization.
