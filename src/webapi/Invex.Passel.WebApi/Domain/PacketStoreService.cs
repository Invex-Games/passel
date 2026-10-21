namespace Invex.Passel.WebApi.Domain;

internal interface IPacketStoreService
{
    Task<PacketAggregate?> Handle(CreateTextPacket command);

    Task<PacketAggregate?> Handle(CreateLinkPacket command);
}

internal sealed class PacketStoreService(IDocumentSession session) : IPacketStoreService
{
    public async Task<PacketAggregate?> Handle(CreateTextPacket command)
    {
        var id = Guid.CreateVersion7();
        var stream = await session.Events.FetchForWriting<PacketAggregate>(id);

        stream.AppendOne(new TextPacketCreated
        {
            UserId = command.UserId,
            Title = command.Title,
            Body = command.Body,
            CreatedAt = DateTimeOffset.UtcNow,
        });

        await session.SaveChangesAsync();

        return await session.Events.FetchLatest<PacketAggregate>(id);
    }

    public async Task<PacketAggregate?> Handle(CreateLinkPacket command)
    {
        var id = Guid.CreateVersion7();
        var stream = await session.Events.FetchForWriting<PacketAggregate>(id);

        stream.AppendOne(new LinkPacketCreated
        {
            UserId = command.UserId,
            Title = command.Title,
            Url = command.Url,
            CreatedAt = DateTimeOffset.UtcNow,
        });

        await session.SaveChangesAsync();

        return await session.Events.FetchLatest<PacketAggregate>(id);
    }
}
