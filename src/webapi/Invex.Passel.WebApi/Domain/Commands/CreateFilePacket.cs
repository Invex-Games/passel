namespace Invex.Passel.WebApi.Domain.Commands;

[PublicAPI]
public sealed record CreateFilePacket
{
    public required string UserId { get; init; }

    public required string? FileName { get; init; }

    public required string ContentType { get; init; }

    public required byte[] Payload { get; init; }
}
