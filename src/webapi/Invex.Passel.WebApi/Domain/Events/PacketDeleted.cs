namespace Invex.Passel.WebApi.Domain.Events;

[PublicAPI]
public sealed record PacketDeleted
{
    public required DateTimeOffset DeletedAt { get; init; }
}
