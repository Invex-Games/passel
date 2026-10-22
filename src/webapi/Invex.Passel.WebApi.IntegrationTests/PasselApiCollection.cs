namespace Invex.Passel.WebApi.IntegrationTests;

/// <summary>
///     Every test class in this collection shares one PostgreSQL container and one API host, held by
///     <see cref="PasselApiFixture" />, and the classes run one after another.
/// </summary>
[CollectionDefinition(Name)]
public sealed class PasselApiCollection : ICollectionFixture<PasselApiFixture>
{
    public const string Name = "Passel API";
}
