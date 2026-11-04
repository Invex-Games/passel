namespace Invex.Passel.WebApi.Domain.Commands;

[PublicAPI]
public sealed record CreateFilePacket
{
    public required string UserId { get; init; }

    public required Guid ParcelId { get; init; }

    /// <summary>
    ///     The name to send the file under, or <c>null</c> to keep the one it was uploaded with.
    /// </summary>
    public required string? FileName { get; init; }
}
