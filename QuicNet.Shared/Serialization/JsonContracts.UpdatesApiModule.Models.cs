// Named JSON messages preserve the existing property names and constructor order.
#nullable enable
namespace QuicNet.Serialization;

internal sealed record UpdatesApiModuleMessage1(bool success, string error);
