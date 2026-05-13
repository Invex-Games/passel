namespace Invex.Passel.WebApi.Domain.Aggregates;

[PublicAPI]
[Union]
[JsonDerivedType(typeof(Link), typeDiscriminator: nameof(Link))]
[JsonDerivedType(typeof(Text), typeDiscriminator: nameof(Text))]
[JsonDerivedType(typeof(File), typeDiscriminator: nameof(File))]
public partial record Packet
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

[PublicAPI]
public sealed class PacketAggregate : SingleStreamProjection<PacketAggregate, Guid>
{
    public Guid Id { get; init; } = Guid.Empty;

    public string UserId { get; init; } = null!;

    public Packet Packet { get; init; } = null!;

    [UsedImplicitly]
    public static PacketAggregate Create(IEvent<LinkPacketCreated> e) =>
        new()
        {
            Id = e.Id,
            UserId = e.Data.UserId,
            Packet = new Packet.Link
            {
                Name = e.Data.Title,
                Url = e.Data.Url,
            },
        };

    [UsedImplicitly]
    public static PacketAggregate Create(IEvent<TextPacketCreated> e) =>
        new()
        {
            Id = e.Id,
            UserId = e.Data.UserId,
            Packet = new Packet.Text
            {
                Title = e.Data.Title,
                Body = e.Data.Body,
            },
        };

    [UsedImplicitly]
    public static PacketAggregate Create(IEvent<FilePacketCreated> e) =>
        new()
        {
            Id = e.Id,
            UserId = e.Data.UserId,
            Packet = new Packet.File
            {
                FileName = e.Data.FileName,
                ContentType = e.Data.ContentType,
                IsImage = e.Data.IsImage,
                ParcelId = e.Data.ParcelId,
            },
        };

    [UsedImplicitly]
    public bool ShouldDelete(PacketDeleted e) =>
        true;
}
