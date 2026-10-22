namespace Invex.Passel.WebApi.Domain;

/// <summary>
///     A packet as stored: one Marten document per packet. The id is a version 7 <see cref="Guid" />, so id order is
///     creation order, which the feed and its cursor rely on. Deleting a packet deletes the document, and nothing
///     else keeps its content (see ADR 0001, packets as documents).
/// </summary>
[PublicAPI]
public sealed class Packet
{
    public required Guid Id { get; init; }

    public required string UserId { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public required PacketContent Content { get; init; }
}

/// <summary>
///     What a packet carries: a link, a text, or a file held in a parcel. Stored inside the document with a
///     <c>$type</c> discriminator.
/// </summary>
[PublicAPI]
[Union]
[JsonDerivedType(typeof(Link), typeDiscriminator: nameof(Link))]
[JsonDerivedType(typeof(Text), typeDiscriminator: nameof(Text))]
[JsonDerivedType(typeof(File), typeDiscriminator: nameof(File))]
public partial record PacketContent
{
    [PublicAPI]
    public sealed partial record Link
    {
        public required string? Name { get; init; }

        public required string Url { get; init; }
    }

    [PublicAPI]
    public sealed partial record Text
    {
        public required string? Title { get; init; }

        public required string Body { get; init; }
    }

    [PublicAPI]
    public sealed partial record File
    {
        public required string? FileName { get; init; }

        public required string ContentType { get; init; }

        public required Guid ParcelId { get; init; }

        public required bool IsImage { get; init; }
    }
}
