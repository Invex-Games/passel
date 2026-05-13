namespace Invex.Passel.WebApi.Domain.Queries;

[PublicAPI]
public sealed partial record QueryPackets
{
    public required string UserId { get; init; }

    public required Guid? Cursor { get; init; }

    [PublicAPI]
    public sealed partial record Result
    {
        public required List<Packet> Packets { get; init; }

        [PublicAPI]
        [Union]
        [JsonDerivedType(typeof(Text), nameof(Text))]
        [JsonDerivedType(typeof(Link), nameof(Link))]
        [SuppressMessage("ReSharper", "InconsistentNaming")]
        public partial record Packet
        {
            public required string Id { get; init; }

            public required string Title { get; init; }

            public sealed partial record Text
            {
                public required string Body { get; init; }
            }

            public sealed partial record Link
            {
                public required string Url { get; init; }
            }
        }
    }
}
