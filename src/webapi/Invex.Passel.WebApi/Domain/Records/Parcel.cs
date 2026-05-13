namespace Invex.Passel.WebApi.Domain.Records;

[PublicAPI]
public sealed record Parcel
{
    public required Guid Id { get; init; }

    public required string ContentType { get; init; }

    public string? FileName { get; init; }

    public required byte[] Data { get; init; }
}
