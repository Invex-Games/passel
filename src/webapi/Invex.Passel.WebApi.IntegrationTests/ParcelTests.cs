using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Invex.Passel.WebApi.Services;
using Invex.Passel.WebApi.Tests;
using Microsoft.Extensions.DependencyInjection;

namespace Invex.Passel.WebApi.IntegrationTests;

/// <summary>
///     Uploaded files through the real API, against a real database and Azurite: uploading, the checks on the way
///     in, and the quotas. The factory holds files to 1 MiB, a user to 3 MiB of them and five unsent files, so every
///     limit is cheap to reach. The tests act as users of their own, so the quotas of one never see another.
/// </summary>
[Collection(PasselApiCollection.Name)]
public sealed class ParcelTests(PasselApiFixture fixture)
{
    private const int MaxParcelBytes = 1024 * 1024;

    private static CancellationToken CancellationToken =>
        TestContext.Current.CancellationToken;

    private IParcelStore Store =>
        fixture.Api.Services.GetRequiredService<IParcelStore>();

    private Guid Decode(string parcelId)
    {
        Assert.True(fixture.Api.Services.GetRequiredService<IIdEncodingService>().TryDecode(parcelId, out var id));

        return id;
    }

    private Task<ParcelQuotaUsage> UsageAsync(string userId) =>
        fixture.Api.Services.GetRequiredService<IParcelQuotaService>().GetUsageAsync(userId, CancellationToken);

    private async Task<int> CountBlobsAsync()
    {
        var count = 0;

        await foreach (var _ in Store.ListAsync(CancellationToken))
            count++;

        return count;
    }

    private async Task<byte[]?> ReadBlobAsync(string key)
    {
        if (await Store.OpenReadAsync(key, CancellationToken) is not { } content)
            return null;

        await using var stream = content.Stream;
        using var copy = new MemoryStream();
        await stream.CopyToAsync(copy, CancellationToken);

        return copy.ToArray();
    }

    private static byte[] RandomBytes(int length)
    {
        var bytes = new byte[length];
        Random.Shared.NextBytes(bytes);

        // Never a known magic number by chance
        bytes[0] = 0;

        return bytes;
    }

    private static async Task<(string Title, string? Detail)> ReadProblemAsync(
        HttpResponseMessage response,
        HttpStatusCode expected)
    {
        using var problem = await PacketApi.ReadJsonAsync(response, expected, CancellationToken);

        return (problem.RootElement.GetProperty("title").GetString()!,
            PacketJson.OptionalString(problem.RootElement, "detail"));
    }

    private static async Task<string> ReadFieldErrorAsync(HttpResponseMessage response, string field)
    {
        using var problem = await PacketApi.ReadJsonAsync(response, HttpStatusCode.BadRequest, CancellationToken);

        var errors = problem.RootElement.GetProperty("errors");

        Assert.Equal([field], ContractTests.PropertyNames(errors));

        return Assert.Single(errors.GetProperty(field).EnumerateArray()).GetString()!;
    }

    [Fact]
    public async Task Requests_without_a_user_are_refused()
    {
        using var client = fixture.Api.CreateClient();

        using var response = await client.PostParcelAsync(TestImages.Png(4, 4), "a.png", "image/png", CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task An_uploaded_image_comes_back_with_its_type_size_and_dimensions_and_a_webp_thumbnail_of_at_most_512_px()
    {
        var user = PasselApiFixture.User("alice");
        using var alice = fixture.Api.CreateClientFor(user);
        var png = TestImages.Png(2000, 1000);

        // The declared type is not what is stored
        var parcel = await alice.UploadAsync(png, "holiday.png", "application/octet-stream", CancellationToken);

        Assert.Equal(new ParcelJson(parcel.ParcelId, "holiday.png", "image/png", png.Length, true, 2000, 1000), parcel);

        var id = Decode(parcel.ParcelId);

        Assert.Equal(png, await ReadBlobAsync(ParcelKeys.Original(id)));

        var thumbnail = await ReadBlobAsync(ParcelKeys.Thumbnail(id));

        Assert.NotNull(thumbnail);

        using var decoded = SkiaSharp.SKBitmap.Decode(thumbnail);

        Assert.Equal((512, 256), (decoded.Width, decoded.Height));
        Assert.Equal("RIFF", Encoding.ASCII.GetString(thumbnail, 0, 4));

        // Counted as one unsent file of its exact size
        Assert.Equal(new ParcelQuotaUsage(1, png.Length), await UsageAsync(user));
    }

    [Fact]
    public async Task A_file_claiming_to_be_an_image_has_no_thumbnail_and_is_an_attachment()
    {
        using var alice = fixture.Api.CreateClientFor(PasselApiFixture.User("alice"));

        var text = Encoding.UTF8.GetBytes("not a picture at all");
        var named = await alice.UploadAsync(text, "photo.png", "image/png", CancellationToken);

        Assert.Equal(new ParcelJson(named.ParcelId, "photo.png", "application/octet-stream", text.Length, false, null, null),
            named);

        // The magic number of a PNG and nothing a decoder can use: the sniffed type stays, but it is no image
        var png = TestImages.Png(8, 8);
        var broken = png[..40];
        var truncated = await alice.UploadAsync(broken, "broken.png", "image/png", CancellationToken);

        Assert.Equal(new ParcelJson(truncated.ParcelId, "broken.png", "image/png", broken.Length, false, null, null),
            truncated);

        Assert.Null(await Store.OpenReadAsync(ParcelKeys.Thumbnail(Decode(named.ParcelId)), CancellationToken));
        Assert.Null(await Store.OpenReadAsync(ParcelKeys.Thumbnail(Decode(truncated.ParcelId)), CancellationToken));
    }

    [Fact]
    public async Task The_file_name_is_made_safe_and_may_be_left_out_or_refused_when_too_long()
    {
        using var alice = fixture.Api.CreateClientFor(PasselApiFixture.User("alice"));

        var bytes = RandomBytes(16);

        var path = await alice.UploadAsync(bytes, "C:\\Users\\me\\report\u202Efdp.exe", "text/plain", CancellationToken);

        Assert.Equal("reportfdp.exe", path.FileName);

        var dots = await alice.UploadAsync(bytes, "..", "text/plain", CancellationToken);

        Assert.Null(dots.FileName);

        using var tooLong = await alice.PostParcelAsync(bytes, new string('a', 257), "text/plain", CancellationToken);

        Assert.Equal("FileName must be at most 256 characters.", await ReadFieldErrorAsync(tooLong, "fileName"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_file_over_the_upload_limit_is_refused_with_413_and_nothing_is_kept(bool withLength)
    {
        var user = PasselApiFixture.User("alice");
        using var alice = fixture.Api.CreateClientFor(user);
        var blobs = await CountBlobsAsync();

        using var response = await alice.PostParcelAsync(RandomBytes(MaxParcelBytes + 1),
            "big.bin",
            "application/octet-stream",
            CancellationToken,
            withLength);

        Assert.Equal(("File too large", "Files can be at most 1 MB."),
            await ReadProblemAsync(response, HttpStatusCode.RequestEntityTooLarge));

        // The reservation is released and no byte of it was stored
        Assert.Equal(new ParcelQuotaUsage(0, 0), await UsageAsync(user));
        Assert.Equal(blobs, await CountBlobsAsync());

        // A file exactly at the limit is taken
        var atLimit = await alice.UploadAsync(RandomBytes(MaxParcelBytes), "fits.bin", "application/octet-stream",
            CancellationToken);

        Assert.Equal(MaxParcelBytes, atLimit.Size);
    }

    [Fact]
    public async Task A_body_declaring_more_than_the_limit_is_refused_before_it_is_read()
    {
        var user = PasselApiFixture.User("alice");
        using var alice = fixture.Api.CreateClientFor(user);

        using var response = await alice.PostParcelAsync(RandomBytes(2 * MaxParcelBytes), "big.bin",
            "application/octet-stream", CancellationToken);

        Assert.Equal(("File too large", "Files can be at most 1 MB."),
            await ReadProblemAsync(response, HttpStatusCode.RequestEntityTooLarge));

        // Refused before a parcel was recorded at all
        Assert.Equal(new ParcelQuotaUsage(0, 0), await UsageAsync(user));
    }

    [Fact]
    public async Task An_upload_past_the_storage_quota_is_refused_with_413()
    {
        var user = PasselApiFixture.User("alice");
        using var alice = fixture.Api.CreateClientFor(user);
        const int size = 1_000_000;

        for (var i = 0; i < 3; i++)
            await alice.UploadAsync(RandomBytes(size), $"part{i}.bin", "application/octet-stream", CancellationToken);

        using var response = await alice.PostParcelAsync(RandomBytes(size), "part3.bin", "application/octet-stream",
            CancellationToken);

        Assert.Equal(("Storage limit reached", "Your files use 2 MB of 3 MB. Delete some files and try again."),
            await ReadProblemAsync(response, HttpStatusCode.RequestEntityTooLarge));

        Assert.Equal(new ParcelQuotaUsage(3, 3 * size), await UsageAsync(user));
    }

    [Fact]
    public async Task Too_many_unsent_files_are_refused_with_429()
    {
        var user = PasselApiFixture.User("alice");
        using var alice = fixture.Api.CreateClientFor(user);

        for (var i = 0; i < 5; i++)
            await alice.UploadAsync(RandomBytes(10), $"note{i}.txt", "text/plain", CancellationToken);

        using var response = await alice.PostParcelAsync(RandomBytes(10), "note5.txt", "text/plain", CancellationToken);

        Assert.Equal(("Too many unsent files",
                "You have 5 files uploaded but not sent. Send or delete them, or wait an hour for them to expire."),
            await ReadProblemAsync(response, HttpStatusCode.TooManyRequests));
    }

    [Fact]
    public async Task An_empty_upload_a_non_multipart_body_or_a_missing_file_part_is_refused()
    {
        var user = PasselApiFixture.User("alice");
        using var alice = fixture.Api.CreateClientFor(user);
        const string sendAsFilePart = "Send the file as the file part of a multipart/form-data body.";

        using (var empty = await alice.PostParcelAsync([], "empty.txt", "text/plain", CancellationToken))
            Assert.Equal("The file is empty.", await ReadFieldErrorAsync(empty, "file"));

        // Any other type of body is turned away by routing, as the endpoint declares it takes multipart/form-data
        using (var raw = await alice.PostAsync(ParcelApi.ParcelsPath,
                   new ByteArrayContent(RandomBytes(10)) { Headers = { ContentType = new("application/octet-stream") } },
                   CancellationToken))
            Assert.Equal("Unsupported Media Type", (await ReadProblemAsync(raw, HttpStatusCode.UnsupportedMediaType)).Title);

        using (var noBoundary = await alice.PostAsync(ParcelApi.ParcelsPath,
                   new ByteArrayContent(RandomBytes(10)) { Headers = { ContentType = new("multipart/form-data") } },
                   CancellationToken))
            Assert.Equal(sendAsFilePart, await ReadFieldErrorAsync(noBoundary, "file"));

        // Multipart by its header, and nothing a reader can find a part in
        var garbage = new ByteArrayContent(Encoding.ASCII.GetBytes("no parts in here"));
        garbage.Headers.ContentType = MediaTypeHeaderValue.Parse("multipart/form-data; boundary=abc");

        using (var unreadable = await alice.PostAsync(ParcelApi.ParcelsPath, garbage, CancellationToken))
            Assert.Equal(sendAsFilePart, await ReadFieldErrorAsync(unreadable, "file"));

        // A form field rather than a file, a file under another name, and a file without its name
        using (var field = new MultipartFormDataContent())
        {
            field.Add(new StringContent("hello"), "file");

            using var response = await alice.PostAsync(ParcelApi.ParcelsPath, field, CancellationToken);

            Assert.Equal(sendAsFilePart, await ReadFieldErrorAsync(response, "file"));
        }

        using (var otherName = new MultipartFormDataContent())
        {
            otherName.Add(new ByteArrayContent(RandomBytes(10)), "attachment", "a.bin");

            using var response = await alice.PostAsync(ParcelApi.ParcelsPath, otherName, CancellationToken);

            Assert.Equal(sendAsFilePart, await ReadFieldErrorAsync(response, "file"));
        }

        using (var nameless = await alice.PostParcelAsync(RandomBytes(10), null, "text/plain", CancellationToken))
            Assert.Equal(sendAsFilePart, await ReadFieldErrorAsync(nameless, "file"));

        // Two files in one upload
        using (var two = new MultipartFormDataContent())
        {
            two.Add(new ByteArrayContent(RandomBytes(10)), "file", "a.bin");
            two.Add(new ByteArrayContent(RandomBytes(10)), "file", "b.bin");

            using var response = await alice.PostAsync(ParcelApi.ParcelsPath, two, CancellationToken);

            Assert.Equal("Send one file per upload.", await ReadFieldErrorAsync(response, "file"));
        }

        // Nothing refused is left counted against the user
        Assert.Equal(new ParcelQuotaUsage(0, 0), await UsageAsync(user));
    }

    [Fact]
    public async Task Every_answer_carries_the_headers_that_keep_a_file_from_running_as_a_page()
    {
        using var alice = fixture.Api.CreateClientFor(PasselApiFixture.User("alice"));

        using var created = await alice.PostParcelAsync(RandomBytes(10), "a.bin", "application/octet-stream",
            CancellationToken);
        using var refused = await alice.PostParcelAsync([], "empty.bin", "application/octet-stream", CancellationToken);

        foreach (var response in new[] { created, refused })
        {
            Assert.Equal("sandbox; default-src 'none'", Assert.Single(response.Headers.GetValues("Content-Security-Policy")));
            Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
            Assert.Equal("no-referrer", Assert.Single(response.Headers.GetValues("Referrer-Policy")));

            // Neither is a file, so neither may be cached
            Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        }

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
    }
}
