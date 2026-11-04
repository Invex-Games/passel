using Invex.Passel.WebApi.Domain;
using Invex.Passel.WebApi.Services;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Invex.Passel.WebApi.IntegrationTests;

/// <summary>
///     The file quota reads the parcel documents with plain SQL, so it depends on how they are stored: the status as
///     a JSON number and an unsent parcel's packet as JSON null. These tests store parcels through the API's own
///     store and read them back both ways. Like the migration tests, they reach past HTTP into the host.
/// </summary>
[Collection(PasselApiCollection.Name)]
public sealed class ParcelQuotaServiceTests(PasselApiFixture fixture)
{
    private static CancellationToken CancellationToken =>
        TestContext.Current.CancellationToken;

    private static Parcel NewParcel(string userId, ParcelStatus status, long size, Guid? attachedPacketId = null) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            UserId = userId,
            CreatedAt = DateTimeOffset.UtcNow,
            Status = status,
            Size = size,
            ContentType = "application/octet-stream",
            FileName = "file.bin",
            AttachedPacketId = attachedPacketId,
        };

    private async Task StoreAsync(params Parcel[] parcels)
    {
        await using var session = fixture.Api.Services.GetRequiredService<IDocumentStore>().LightweightSession();
        session.Insert(parcels);
        await session.SaveChangesAsync(CancellationToken);
    }

    private async Task<(string Status, string AttachedPacketId)> JsonTypesAsync(Guid id)
    {
        await using var command = fixture
            .Api
            .Services
            .GetRequiredService<NpgsqlDataSource>()
            .CreateCommand("""
                select jsonb_typeof(data -> 'Status'), coalesce(jsonb_typeof(data -> 'AttachedPacketId'), 'absent')
                from mt_doc_parcel where id = $1
                """);

        command.Parameters.Add(new() { Value = id });

        await using var reader = await command.ExecuteReaderAsync(CancellationToken);
        Assert.True(await reader.ReadAsync(CancellationToken));

        return (reader.GetString(0), reader.GetString(1));
    }

    [Fact]
    public async Task The_status_is_stored_as_a_number_and_an_unsent_parcel_has_a_null_packet()
    {
        var userId = PasselApiFixture.User("parcel-json");
        var unsent = NewParcel(userId, ParcelStatus.Uploaded, 1);
        var sent = NewParcel(userId, ParcelStatus.Uploaded, 1, Guid.CreateVersion7());

        await StoreAsync(unsent, sent);

        // Read with ->>, a JSON null and a missing key are both SQL null; this pins the one the store writes
        Assert.Equal(("number", "null"), await JsonTypesAsync(unsent.Id));
        Assert.Equal(("number", "string"), await JsonTypesAsync(sent.Id));
    }

    [Fact]
    public async Task Usage_counts_unsent_files_and_the_bytes_of_every_file_not_deleted()
    {
        var userId = PasselApiFixture.User("parcel-usage");

        await StoreAsync(
            NewParcel(userId, ParcelStatus.Uploading, 100),
            NewParcel(userId, ParcelStatus.Uploaded, 200),
            NewParcel(userId, ParcelStatus.Uploaded, 400, Guid.CreateVersion7()),
            NewParcel(userId, ParcelStatus.Deleted, 800),
            NewParcel(PasselApiFixture.User("someone-else"), ParcelStatus.Uploaded, 1600));

        var usage = await fixture
            .Api
            .Services
            .GetRequiredService<IParcelQuotaService>()
            .GetUsageAsync(userId, CancellationToken);

        Assert.Equal(new ParcelQuotaUsage(2, 700), usage);
    }

    [Fact]
    public async Task A_user_without_files_uses_nothing()
    {
        var usage = await fixture
            .Api
            .Services
            .GetRequiredService<IParcelQuotaService>()
            .GetUsageAsync(PasselApiFixture.User("parcel-none"), CancellationToken);

        Assert.Equal(new ParcelQuotaUsage(0, 0), usage);
    }
}
