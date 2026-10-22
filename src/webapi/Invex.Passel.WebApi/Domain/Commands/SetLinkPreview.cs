namespace Invex.Passel.WebApi.Domain.Commands;

/// <summary>
///     Attaches the preview fetched for a link to its packet. Issued by the preview worker, never by a request.
/// </summary>
[PublicAPI]
public sealed record SetLinkPreview
{
    public required string UserId { get; init; }

    public required Guid PacketId { get; init; }

    public required LinkPreview Preview { get; init; }
}
