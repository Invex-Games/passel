using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Invex.Passel.WebApi.Domain;
using Invex.Passel.WebApi.Services;
using Invex.Passel.WebApi.Tests;
using Marten;
using Microsoft.Extensions.DependencyInjection;

namespace Invex.Passel.WebApi.IntegrationTests;

/// <summary>
///     Uploaded files through the real API, against a real database and Azurite: uploading, the checks on the way
///     in, the quotas, downloads and thumbnails, discarding, sending a file in a packet and deleting that packet, and
///     the sweep that expires and erases files. The factory holds files to 1 MiB, a user to 3 MiB of them and five
///     unsent files, so every limit is cheap to reach. The tests act as users of their own, so the quotas of one never
///     see another. The shared host's sweep is off; the tests that need it run it themselves, at a time of their
///     choosing, which reaches the parcels of every test before them too, so they assert only on their own.
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

    /// <summary>
    ///     Runs the sweep as of <paramref name="now" />, in a scope of its own as the worker does, until it finds
    ///     nothing more to expire or erase: the shared database holds the files of every test before this one.
    /// </summary>
    private async Task SweepAsync(DateTimeOffset now)
    {
        for (var run = 0; run < 20; run++)
        {
            await using var scope = fixture.Api.Services.CreateAsyncScope();
            var result = await scope.ServiceProvider.GetRequiredService<ParcelSweep>().RunOnceAsync(now, CancellationToken);

            Assert.Equal(0, result.Failed);

            if (result is { Expired: 0, Erased: 0 })
                return;
        }

        Assert.Fail("The sweep kept finding work.");
    }

    private async Task<Parcel?> LoadParcelAsync(Guid id)
    {
        await using var session = fixture.Api.Services.GetRequiredService<IDocumentStore>().QuerySession();

        return await session.LoadAsync<Parcel>(id, CancellationToken);
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

    /// <summary>
    ///     The Cache-Control header as the API wrote it, which the client would otherwise reorder.
    /// </summary>
    private static string CacheControl(HttpResponseMessage response) =>
        response.Headers.NonValidated["Cache-Control"].ToString();

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

        using var response = await alice.GetThumbnailAsync(parcel.ParcelId, CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/webp", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("inline; filename=thumbnail.webp; filename*=UTF-8''thumbnail.webp",
            response.Content.Headers.ContentDisposition?.ToString());

        var thumbnail = await response.Content.ReadAsByteArrayAsync(CancellationToken);
        using var decoded = SkiaSharp.SKBitmap.Decode(thumbnail);

        Assert.Equal((512, 256), (decoded.Width, decoded.Height));
        Assert.Equal(thumbnail, await ReadBlobAsync(ParcelKeys.Thumbnail(id)));

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

        foreach (var parcel in new[] { named, truncated })
        {
            Assert.Null(await Store.OpenReadAsync(ParcelKeys.Thumbnail(Decode(parcel.ParcelId)), CancellationToken));

            using var thumbnail = await alice.GetThumbnailAsync(parcel.ParcelId, CancellationToken);

            Assert.Equal(HttpStatusCode.NotFound, thumbnail.StatusCode);

            using var download = await alice.DownloadParcelAsync(parcel.ParcelId, CancellationToken);

            Assert.Equal(HttpStatusCode.OK, download.StatusCode);
            Assert.Equal("attachment", download.Content.Headers.ContentDisposition?.DispositionType);
        }
    }

    [Fact]
    public async Task An_uploaded_file_downloads_byte_for_byte_with_an_etag_immutable_caching_a_sandbox_and_nosniff()
    {
        using var alice = fixture.Api.CreateClientFor(PasselApiFixture.User("alice"));
        var png = TestImages.Png(64, 32);
        var parcel = await alice.UploadAsync(png, "Café.png", "image/png", CancellationToken);

        using var response = await alice.DownloadParcelAsync(parcel.ParcelId, CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(png, await response.Content.ReadAsByteArrayAsync(CancellationToken));

        Assert.Equal($"\"{Convert.ToHexStringLower(SHA256.HashData(png))}\"", response.Headers.ETag?.Tag);
        Assert.False(response.Headers.ETag?.IsWeak);
        Assert.Equal("private, max-age=31536000, immutable", CacheControl(response));
        Assert.Contains("Authorization", response.Headers.Vary);
        Assert.Equal("bytes", Assert.Single(response.Headers.AcceptRanges));
        Assert.Equal("sandbox; default-src 'none'", Assert.Single(response.Headers.GetValues("Content-Security-Policy")));
        Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
        Assert.Equal("no-referrer", Assert.Single(response.Headers.GetValues("Referrer-Policy")));

        Assert.Equal("image/png", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(png.Length, response.Content.Headers.ContentLength);

        // An image is shown in place, under its name exactly and in ASCII for older clients
        Assert.Equal("inline; filename=Caf_.png; filename*=UTF-8''Caf%C3%A9.png",
            response.Content.Headers.ContentDisposition?.ToString());
    }

    [Fact]
    public async Task A_range_request_gets_206_and_an_unsatisfiable_one_416()
    {
        using var alice = fixture.Api.CreateClientFor(PasselApiFixture.User("alice"));
        var bytes = RandomBytes(1000);
        var parcel = await alice.UploadAsync(bytes, "data.bin", "application/octet-stream", CancellationToken);

        using var partial = await alice.DownloadParcelAsync(parcel.ParcelId,
            CancellationToken,
            request => request.Headers.Range = new(100, 199));

        Assert.Equal(HttpStatusCode.PartialContent, partial.StatusCode);
        Assert.Equal("bytes 100-199/1000", partial.Content.Headers.ContentRange?.ToString());
        Assert.Equal(bytes[100..200], await partial.Content.ReadAsByteArrayAsync(CancellationToken));
        Assert.Equal("private, max-age=31536000, immutable", CacheControl(partial));

        using var beyond = await alice.DownloadParcelAsync(parcel.ParcelId,
            CancellationToken,
            request => request.Headers.Range = new(5000, null));

        Assert.Equal(HttpStatusCode.RequestedRangeNotSatisfiable, beyond.StatusCode);
        Assert.Equal("bytes */1000", beyond.Content.Headers.ContentRange?.ToString());
        Assert.Equal("no-store", CacheControl(beyond));
    }

    [Fact]
    public async Task A_request_with_the_etag_gets_304()
    {
        using var alice = fixture.Api.CreateClientFor(PasselApiFixture.User("alice"));
        var parcel = await alice.UploadAsync(TestImages.Png(600, 300), "wide.png", "image/png", CancellationToken);

        using var first = await alice.DownloadParcelAsync(parcel.ParcelId, CancellationToken);
        using var firstThumbnail = await alice.GetThumbnailAsync(parcel.ParcelId, CancellationToken);

        var tag = first.Headers.ETag!;
        var thumbnailTag = firstThumbnail.Headers.ETag!;

        // The thumbnail has a tag of its own
        Assert.NotEqual(tag.Tag, thumbnailTag.Tag);
        Assert.Equal($"\"{tag.Tag.Trim('"')}-t512\"", thumbnailTag.Tag);

        using var again = await alice.DownloadParcelAsync(parcel.ParcelId,
            CancellationToken,
            request => request.Headers.IfNoneMatch.Add(tag));

        Assert.Equal(HttpStatusCode.NotModified, again.StatusCode);
        Assert.Empty(await again.Content.ReadAsByteArrayAsync(CancellationToken));
        Assert.Equal("private, max-age=31536000, immutable", CacheControl(again));

        using var thumbnailRequest = new HttpRequestMessage(HttpMethod.Get,
            $"{ParcelApi.ParcelsPath}/{parcel.ParcelId}/thumbnail");
        thumbnailRequest.Headers.IfNoneMatch.Add(thumbnailTag);

        using var thumbnailAgain = await alice.SendAsync(thumbnailRequest, CancellationToken);

        Assert.Equal(HttpStatusCode.NotModified, thumbnailAgain.StatusCode);
    }

    [Fact]
    public async Task An_html_file_is_stored_as_octet_stream_and_served_as_an_attachment_uncompressed()
    {
        using var alice = fixture.Api.CreateClientFor(PasselApiFixture.User("alice"));
        var page = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("<html><script>alert(1)</script></html>\n", 200)));

        var parcel = await alice.UploadAsync(page, "page.html", "text/html", CancellationToken);

        Assert.Equal("application/octet-stream", parcel.ContentType);
        Assert.False(parcel.IsImage);

        using var response = await alice.DownloadParcelAsync(parcel.ParcelId,
            CancellationToken,
            request => request.Headers.AcceptEncoding.Add(new("gzip")));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(response.Content.Headers.ContentEncoding);
        Assert.Equal(page.Length, response.Content.Headers.ContentLength);
        Assert.Equal(page, await response.Content.ReadAsByteArrayAsync(CancellationToken));
        Assert.Equal("application/octet-stream", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("attachment; filename=page.html; filename*=UTF-8''page.html",
            response.Content.Headers.ContentDisposition?.ToString());
    }

    [Fact]
    public async Task An_image_thumbnail_carries_no_metadata()
    {
        using var alice = fixture.Api.CreateClientFor(PasselApiFixture.User("alice"));

        // Turned a quarter clockwise by its EXIF orientation, with a GPS position
        var jpeg = TestImages.JpegWithExif(2000, 1000, 6);
        var parcel = await alice.UploadAsync(jpeg, "camera.jpg", "image/jpeg", CancellationToken);

        Assert.Equal(new ParcelJson(parcel.ParcelId, "camera.jpg", "image/jpeg", jpeg.Length, true, 1000, 2000), parcel);

        // The original keeps every byte, its metadata included
        using (var download = await alice.DownloadParcelAsync(parcel.ParcelId, CancellationToken))
            Assert.Equal(jpeg, await download.Content.ReadAsByteArrayAsync(CancellationToken));

        using var response = await alice.GetThumbnailAsync(parcel.ParcelId, CancellationToken);
        var thumbnail = await response.Content.ReadAsByteArrayAsync(CancellationToken);

        Assert.Equal(["VP8 "], TestImages.RiffChunks(thumbnail));

        using var decoded = SkiaSharp.SKBitmap.Decode(thumbnail);

        Assert.Equal((256, 512), (decoded.Width, decoded.Height));
    }

    [Fact]
    public async Task Another_users_parcel_is_not_found()
    {
        using var alice = fixture.Api.CreateClientFor(PasselApiFixture.User("alice"));
        using var bob = fixture.Api.CreateClientFor(PasselApiFixture.User("bob"));

        var parcel = await alice.UploadAsync(TestImages.Png(16, 16), "mine.png", "image/png", CancellationToken);

        using (var download = await bob.DownloadParcelAsync(parcel.ParcelId, CancellationToken))
            Assert.Equal(HttpStatusCode.NotFound, download.StatusCode);

        using (var thumbnail = await bob.GetThumbnailAsync(parcel.ParcelId, CancellationToken))
            Assert.Equal(HttpStatusCode.NotFound, thumbnail.StatusCode);

        Assert.Equal(HttpStatusCode.NotFound, await bob.DiscardParcelAsync(parcel.ParcelId, CancellationToken));

        // Nor can it be sent by anyone else, who is told what any missing file gets
        using (var sent = await bob.CreateFileAsync(parcel.ParcelId, null, CancellationToken))
            Assert.Equal(NoSuchFileToSend, await ReadFieldErrorAsync(sent, "parcelId"));

        Assert.Empty((await bob.ListAsync(null, CancellationToken)).Packets);

        // Still there for its owner, and still theirs to send
        using var own = await alice.DownloadParcelAsync(parcel.ParcelId, CancellationToken);

        Assert.Equal(HttpStatusCode.OK, own.StatusCode);

        await alice.SendFileAsync(parcel.ParcelId, null, CancellationToken);
    }

    [Fact]
    public async Task An_id_that_is_not_a_parcel_of_the_user_is_not_found()
    {
        using var alice = fixture.Api.CreateClientFor(PasselApiFixture.User("alice"));

        var packet = await alice.CreateTextAsync(null, "not a file", CancellationToken);
        var unknown = fixture.Api.Services.GetRequiredService<IIdEncodingService>().Encode(Guid.CreateVersion7());

        foreach (var id in new[] { "not-a-parcel-id", unknown, packet.Id })
        {
            using var download = await alice.DownloadParcelAsync(id, CancellationToken);
            using var thumbnail = await alice.GetThumbnailAsync(id, CancellationToken);

            Assert.Equal(HttpStatusCode.NotFound, download.StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, thumbnail.StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, await alice.DiscardParcelAsync(id, CancellationToken));

            // Even a miss is not cached, and is sandboxed like a file
            Assert.Equal("no-store", CacheControl(download));
            Assert.Equal("sandbox; default-src 'none'", Assert.Single(download.Headers.GetValues("Content-Security-Policy")));
        }
    }

    [Fact]
    public async Task A_discarded_upload_cannot_be_sent_or_downloaded()
    {
        var user = PasselApiFixture.User("alice");
        using var alice = fixture.Api.CreateClientFor(user);

        var parcel = await alice.UploadAsync(TestImages.Png(16, 16), "mistake.png", "image/png", CancellationToken);

        Assert.Equal(HttpStatusCode.NoContent, await alice.DiscardParcelAsync(parcel.ParcelId, CancellationToken));

        using (var sent = await alice.CreateFileAsync(parcel.ParcelId, null, CancellationToken))
            Assert.Equal(NoSuchFileToSend, await ReadFieldErrorAsync(sent, "parcelId"));

        Assert.Empty((await alice.ListAsync(null, CancellationToken)).Packets);

        using (var download = await alice.DownloadParcelAsync(parcel.ParcelId, CancellationToken))
            Assert.Equal(HttpStatusCode.NotFound, download.StatusCode);

        using (var thumbnail = await alice.GetThumbnailAsync(parcel.ParcelId, CancellationToken))
            Assert.Equal(HttpStatusCode.NotFound, thumbnail.StatusCode);

        Assert.Equal(HttpStatusCode.NotFound, await alice.DiscardParcelAsync(parcel.ParcelId, CancellationToken));

        // No longer counted: neither as unsent nor in the storage used
        Assert.Equal(new ParcelQuotaUsage(0, 0), await UsageAsync(user));
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

    /// <summary>
    ///     Further over the limit than one byte, the read that crosses it carries more than the one byte past it, and
    ///     the count of the file still decides: the multipart reader must not refuse it first as a malformed body. With
    ///     a length, the larger two are refused before the body is read; without one, all three are read until the
    ///     count stops them.
    /// </summary>
    [Theory]
    [InlineData(2, true)]
    [InlineData(2, false)]
    [InlineData(100 * 1024, true)]
    [InlineData(100 * 1024, false)]
    [InlineData(MaxParcelBytes, true)]
    [InlineData(MaxParcelBytes, false)]
    public async Task A_file_well_over_the_upload_limit_is_refused_with_413_and_nothing_is_kept(int over, bool withLength)
    {
        var user = PasselApiFixture.User("alice");
        using var alice = fixture.Api.CreateClientFor(user);
        var blobs = await CountBlobsAsync();

        using var response = await alice.PostParcelAsync(RandomBytes(MaxParcelBytes + over),
            "big.bin",
            "application/octet-stream",
            CancellationToken,
            withLength);

        Assert.Equal(("File too large", "Files can be at most 1 MB."),
            await ReadProblemAsync(response, HttpStatusCode.RequestEntityTooLarge));

        Assert.Equal(new ParcelQuotaUsage(0, 0), await UsageAsync(user));
        Assert.Equal(blobs, await CountBlobsAsync());
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
            Assert.Equal("no-store", CacheControl(response));
        }

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
    }

    private const string NoSuchFileToSend =
        "No such file to send: it may have expired or been deleted. Upload it again.";

    [Fact]
    public async Task A_file_packet_is_created_from_an_upload_listed_and_announced()
    {
        var user = PasselApiFixture.User("alice");
        using var alice = fixture.Api.CreateClientFor(user);

        await using var stream = await EventStream.OpenAsync(alice, CancellationToken);
        await stream.ReadAsync(CancellationToken);

        var parcel = await alice.UploadAsync(TestImages.Png(40, 20), "screenshot.png", "image/png", CancellationToken);

        using var response = await alice.CreateFileAsync(parcel.ParcelId, null, CancellationToken);
        using var document = await PacketApi.ReadJsonAsync(response, HttpStatusCode.OK, CancellationToken);

        Assert.Equal(["$type", "contentType", "createdAt", "fileName", "id", "isImage", "parcelId"],
            ContractTests.PropertyNames(document.RootElement));

        var packet = PacketJson.Parse(document.RootElement);

        Assert.Equal(new PacketJson("File", packet.Id, packet.CreatedAt, null, null, null, null, null,
                "screenshot.png", "image/png", parcel.ParcelId, true),
            packet);

        // Announced to the user's streams like any other packet, in the same shape
        var created = await stream.ReadAsync(CancellationToken);

        Assert.Equal(EventStream.PacketCreatedEventType, created.EventType);
        Assert.Equal(packet, PacketJson.Parse(created.Data));

        Assert.Equal([packet], (await alice.ListAsync(null, CancellationToken)).Packets);

        // A sent file is no longer counted as unsent, but its bytes still count, and it still downloads
        Assert.Equal(new ParcelQuotaUsage(0, parcel.Size), await UsageAsync(user));

        using var download = await alice.DownloadParcelAsync(parcel.ParcelId, CancellationToken);

        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
    }

    [Fact]
    public async Task A_file_packet_takes_its_type_and_image_flag_from_the_parcel_and_may_rename_it()
    {
        using var alice = fixture.Api.CreateClientFor(PasselApiFixture.User("alice"));

        // Claimed to be a picture, and sent under another name: neither claim makes it an image
        var text = await alice.UploadAsync(Encoding.UTF8.GetBytes("plain words"), "photo.png", "image/png",
            CancellationToken);
        var renamed = await alice.SendFileAsync(text.ParcelId, "C:\\notes\\list\u202E.txt", CancellationToken);

        Assert.Equal(("list.txt", "application/octet-stream", text.ParcelId, false),
            (renamed.FileName, renamed.ContentType, renamed.ParcelId, renamed.IsImage));

        // The rename is the packet's; the file keeps the name it was uploaded with
        using (var download = await alice.DownloadParcelAsync(text.ParcelId, CancellationToken))
            Assert.Equal("photo.png", download.Content.Headers.ContentDisposition?.FileNameStar);

        // Without a name, or with one that is nothing once made safe, the upload's name is kept
        var jpeg = await alice.UploadAsync(TestImages.JpegWithExif(30, 20, 1), "camera.jpg", "application/octet-stream",
            CancellationToken);
        var kept = await alice.SendFileAsync(jpeg.ParcelId, null, CancellationToken);

        Assert.Equal(("camera.jpg", "image/jpeg", true), (kept.FileName, kept.ContentType, kept.IsImage));

        var nameless = await alice.UploadAsync(TestImages.Png(4, 4), "..", "image/png", CancellationToken);

        Assert.Null(nameless.FileName);

        var dots = await alice.SendFileAsync(nameless.ParcelId, "..", CancellationToken);

        Assert.Null(dots.FileName);

        // The listed packets say the same
        var listed = (await alice.ListAsync(null, CancellationToken)).Packets;

        Assert.Equal([dots, kept, renamed], listed);
    }

    [Fact]
    public async Task A_file_packet_is_refused_for_an_id_that_names_no_file_or_a_name_that_is_too_long()
    {
        using var alice = fixture.Api.CreateClientFor(PasselApiFixture.User("alice"));
        const string mustBeUploaded = "ParcelId must be the id of a file you uploaded.";

        var parcel = await alice.UploadAsync(RandomBytes(10), "a.bin", "application/octet-stream", CancellationToken);

        foreach (var id in new[] { string.Empty, "not-a-parcel-id", new string('a', 65) })
        {
            using var response = await alice.CreateFileAsync(id, null, CancellationToken);

            Assert.Equal(mustBeUploaded, await ReadFieldErrorAsync(response, "parcelId"));
        }

        // A real id of something else, and one of no file at all
        var text = await alice.CreateTextAsync(null, "not a file", CancellationToken);
        var unknown = fixture.Api.Services.GetRequiredService<IIdEncodingService>().Encode(Guid.CreateVersion7());

        foreach (var id in new[] { text.Id, unknown })
        {
            using var response = await alice.CreateFileAsync(id, null, CancellationToken);

            Assert.Equal(NoSuchFileToSend, await ReadFieldErrorAsync(response, "parcelId"));
        }

        using (var tooLong = await alice.CreateFileAsync(parcel.ParcelId, new string('a', 257), CancellationToken))
            Assert.Equal("FileName must be at most 256 characters.", await ReadFieldErrorAsync(tooLong, "fileName"));

        // Nothing refused was sent
        Assert.Equal([text.Id], (await alice.ListAsync(null, CancellationToken)).Packets.Select(packet => packet.Id));
    }

    [Fact]
    public async Task A_file_that_is_still_uploading_or_deleted_cannot_be_sent()
    {
        var user = PasselApiFixture.User("alice");
        using var alice = fixture.Api.CreateClientFor(user);
        var encoding = fixture.Api.Services.GetRequiredService<IIdEncodingService>();

        var uploading = NewParcel(user, ParcelStatus.Uploading);
        var deleted = NewParcel(user, ParcelStatus.Deleted);

        await using (var session = fixture.Api.Services.GetRequiredService<IDocumentStore>().LightweightSession())
        {
            session.Insert(uploading, deleted);
            await session.SaveChangesAsync(CancellationToken);
        }

        foreach (var parcel in new[] { uploading, deleted })
        {
            using var response = await alice.CreateFileAsync(encoding.Encode(parcel.Id), null, CancellationToken);

            Assert.Equal(NoSuchFileToSend, await ReadFieldErrorAsync(response, "parcelId"));
        }

        Assert.Empty((await alice.ListAsync(null, CancellationToken)).Packets);
    }

    [Fact]
    public async Task A_parcel_is_sent_only_once()
    {
        var user = PasselApiFixture.User("alice");
        using var alice = fixture.Api.CreateClientFor(user);

        var parcel = await alice.UploadAsync(TestImages.Png(16, 16), "once.png", "image/png", CancellationToken);
        var sent = await alice.SendFileAsync(parcel.ParcelId, null, CancellationToken);

        using (var again = await alice.CreateFileAsync(parcel.ParcelId, "twice.png", CancellationToken))
            Assert.Equal(("File already sent", "This file is already in another item; upload it again to send it twice."),
                await ReadProblemAsync(again, HttpStatusCode.Conflict));

        // A sent file is discarded by deleting its packet, not on its own
        using (var discard = await alice.DeleteAsync($"{ParcelApi.ParcelsPath}/{parcel.ParcelId}", CancellationToken))
            Assert.Equal(("Delete the item instead", "This file has been sent; delete the item it is in."),
                await ReadProblemAsync(discard, HttpStatusCode.Conflict));

        Assert.Equal([sent], (await alice.ListAsync(null, CancellationToken)).Packets);

        // Sends of one file at the same time: one wins, and the other is told the file has gone already. Several
        // rounds, so that some of them read the file before either has written
        const int rounds = 8;
        var winners = new List<string>();

        for (var round = 0; round < rounds; round++)
        {
            var raced = await alice.UploadAsync(RandomBytes(10), $"raced{round}.bin", "application/octet-stream",
                CancellationToken);

            var responses = await Task.WhenAll(Enumerable
                .Range(0, 2)
                .Select(_ => Task.Run(() => alice.CreateFileAsync(raced.ParcelId, null, CancellationToken),
                    CancellationToken)));

            try
            {
                Assert.Equal([HttpStatusCode.OK, HttpStatusCode.Conflict],
                    responses.Select(response => response.StatusCode).Order());

                using var won = await PacketApi.ReadJsonAsync(responses.Single(response => response.IsSuccessStatusCode),
                    HttpStatusCode.OK,
                    CancellationToken);

                winners.Add(PacketJson.Parse(won.RootElement).Id);
            }
            finally
            {
                foreach (var response in responses)
                    response.Dispose();
            }
        }

        // One packet per file, and nothing else
        var listed = (await alice.ListAsync(null, CancellationToken)).Packets;

        Assert.Equal(winners.Append(sent.Id).Order(), listed.Select(packet => packet.Id).Order());
    }

    /// <summary>
    ///     The version check, not just the attached-packet check, is what decides between two sends: a send whose read
    ///     of the file is older than another's commit must lose, even though the file it read was unsent.
    /// </summary>
    [Fact]
    public async Task A_send_that_read_the_file_before_another_send_committed_loses()
    {
        var user = PasselApiFixture.User("alice");
        using var alice = fixture.Api.CreateClientFor(user);

        var parcel = await alice.UploadAsync(RandomBytes(10), "raced.bin", "application/octet-stream", CancellationToken);
        var id = Decode(parcel.ParcelId);

        await using var session = fixture.Api.Services.GetRequiredService<IDocumentStore>().LightweightSession();
        var stale = await session.LoadAsync<Parcel>(id, CancellationToken);

        Assert.NotNull(stale);
        Assert.Null(stale.AttachedPacketId);

        var sent = await alice.SendFileAsync(parcel.ParcelId, null, CancellationToken);

        // What the losing send writes: its own packet, and the file attached to it, from its stale read
        var packet = new Packet
        {
            Id = Guid.CreateVersion7(),
            UserId = user,
            CreatedAt = DateTimeOffset.UtcNow,
            Content = new PacketContent.File
            {
                FileName = stale.FileName,
                ContentType = stale.ContentType,
                ParcelId = stale.Id,
                IsImage = stale.IsImage,
            },
        };

        session.Store(packet);
        session.Update(stale with
        {
            AttachedPacketId = packet.Id,
        });

        var lost = await Assert.ThrowsAnyAsync<Exception>(() => session.SaveChangesAsync(CancellationToken));

        Assert.True(MartenRaces.IsLostRace(lost), lost.ToString());

        // Neither its packet nor its attachment was written
        Assert.Equal([sent], (await alice.ListAsync(null, CancellationToken)).Packets);

        await using var check = fixture.Api.Services.GetRequiredService<IDocumentStore>().QuerySession();

        Assert.Equal(Decode(sent.Id), (await check.LoadAsync<Parcel>(id, CancellationToken))?.AttachedPacketId);
    }

    [Fact]
    public async Task Deleting_a_file_packet_makes_its_file_unavailable_at_once_and_erases_it()
    {
        var user = PasselApiFixture.User("alice");
        using var alice = fixture.Api.CreateClientFor(user);

        await using var stream = await EventStream.OpenAsync(alice, CancellationToken);
        await stream.ReadAsync(CancellationToken);

        var parcel = await alice.UploadAsync(TestImages.Png(600, 300), "gone.png", "image/png", CancellationToken);
        var packet = await alice.SendFileAsync(parcel.ParcelId, null, CancellationToken);

        Assert.Equal(EventStream.PacketCreatedEventType, (await stream.ReadAsync(CancellationToken)).EventType);

        Assert.Equal(HttpStatusCode.NoContent, await alice.DeletePacketAsync(packet.Id, CancellationToken));

        var deleted = await stream.ReadAsync(CancellationToken);

        Assert.Equal(EventStream.PacketDeletedEventType, deleted.EventType);

        using (var deletedData = JsonDocument.Parse(deleted.Data))
            Assert.Equal(packet.Id, deletedData.RootElement.GetProperty("id").GetString());

        // Gone for every purpose at once, though its bytes wait for the sweep
        using (var download = await alice.DownloadParcelAsync(parcel.ParcelId, CancellationToken))
            Assert.Equal(HttpStatusCode.NotFound, download.StatusCode);

        using (var thumbnail = await alice.GetThumbnailAsync(parcel.ParcelId, CancellationToken))
            Assert.Equal(HttpStatusCode.NotFound, thumbnail.StatusCode);

        Assert.Equal(HttpStatusCode.NotFound, await alice.DiscardParcelAsync(parcel.ParcelId, CancellationToken));

        using (var resent = await alice.CreateFileAsync(parcel.ParcelId, null, CancellationToken))
            Assert.Equal(NoSuchFileToSend, await ReadFieldErrorAsync(resent, "parcelId"));

        Assert.Equal(new ParcelQuotaUsage(0, 0), await UsageAsync(user));
        Assert.Equal(HttpStatusCode.NotFound, await alice.DeletePacketAsync(packet.Id, CancellationToken));

        // The delete woke the sweep, which is off on this host; run as it would, it erases the bytes and the document
        var id = Decode(parcel.ParcelId);

        Assert.NotNull(await ReadBlobAsync(ParcelKeys.Original(id)));
        Assert.NotNull(await ReadBlobAsync(ParcelKeys.Thumbnail(id)));

        await SweepAsync(DateTimeOffset.UtcNow);

        Assert.Null(await Store.OpenReadAsync(ParcelKeys.Original(id), CancellationToken));
        Assert.Null(await Store.OpenReadAsync(ParcelKeys.Thumbnail(id), CancellationToken));
        Assert.Null(await LoadParcelAsync(id));
    }

    [Fact]
    public async Task An_unsent_upload_expires_after_an_hour_and_is_erased()
    {
        var user = PasselApiFixture.User("alice");
        using var alice = fixture.Api.CreateClientFor(user);
        var now = DateTimeOffset.UtcNow;

        var unsent = await alice.UploadAsync(TestImages.Png(600, 300), "waiting.png", "image/png", CancellationToken);
        var sentParcel = await alice.UploadAsync(TestImages.Png(600, 300), "sent.png", "image/png", CancellationToken);
        var packet = await alice.SendFileAsync(sentParcel.ParcelId, null, CancellationToken);

        // An upload whose instance stopped part way, as the sweep finds it
        var stuck = NewParcel(user, ParcelStatus.Uploading);

        await using (var session = fixture.Api.Services.GetRequiredService<IDocumentStore>().LightweightSession())
        {
            session.Insert(stuck);
            await session.SaveChangesAsync(CancellationToken);
        }

        // Past the grace an upload has to finish, and well within the hour an unsent file waits
        await SweepAsync(now + TimeSpan.FromMinutes(16));

        Assert.Null(await LoadParcelAsync(stuck.Id));

        using (var waiting = await alice.DownloadParcelAsync(unsent.ParcelId, CancellationToken))
            Assert.Equal(HttpStatusCode.OK, waiting.StatusCode);

        // Past the hour
        await SweepAsync(now + TimeSpan.FromHours(2));

        var unsentId = Decode(unsent.ParcelId);

        using (var expired = await alice.DownloadParcelAsync(unsent.ParcelId, CancellationToken))
            Assert.Equal(HttpStatusCode.NotFound, expired.StatusCode);

        using (var send = await alice.CreateFileAsync(unsent.ParcelId, null, CancellationToken))
            Assert.Equal(NoSuchFileToSend, await ReadFieldErrorAsync(send, "parcelId"));

        Assert.Null(await Store.OpenReadAsync(ParcelKeys.Original(unsentId), CancellationToken));
        Assert.Null(await Store.OpenReadAsync(ParcelKeys.Thumbnail(unsentId), CancellationToken));
        Assert.Null(await LoadParcelAsync(unsentId));

        // A sent file never expires
        using (var kept = await alice.DownloadParcelAsync(sentParcel.ParcelId, CancellationToken))
            Assert.Equal(HttpStatusCode.OK, kept.StatusCode);

        using (var thumbnail = await alice.GetThumbnailAsync(sentParcel.ParcelId, CancellationToken))
            Assert.Equal(HttpStatusCode.OK, thumbnail.StatusCode);

        Assert.Equal([packet], (await alice.ListAsync(null, CancellationToken)).Packets);
        Assert.Equal(new ParcelQuotaUsage(0, sentParcel.Size), await UsageAsync(user));
    }

    /// <summary>
    ///     The sweep finds expired files with a query, and writes each one from what that query read. A file sent
    ///     between the two must make the write lose, not be expired under its packet: the sweep's own steps are run
    ///     with the send in between.
    /// </summary>
    [Fact]
    public async Task The_sweep_cannot_expire_a_file_that_was_sent_after_it_found_it()
    {
        var user = PasselApiFixture.User("alice");
        using var alice = fixture.Api.CreateClientFor(user);
        var later = DateTimeOffset.UtcNow + TimeSpan.FromHours(2);

        // Whatever earlier tests left unsent goes first, so the one batch found below holds this test's file
        await SweepAsync(later);

        var parcel = await alice.UploadAsync(TestImages.Png(600, 300), "raced.png", "image/png", CancellationToken);
        var id = Decode(parcel.ParcelId);

        await using var scope = fixture.Api.Services.CreateAsyncScope();
        var sweep = scope.ServiceProvider.GetRequiredService<ParcelSweep>();

        var found = Assert.Single(await sweep.FindExpirableAsync(later, CancellationToken), candidate => candidate.Id == id);

        Assert.Null(found.AttachedPacketId);

        var packet = await alice.SendFileAsync(parcel.ParcelId, null, CancellationToken);

        Assert.False(await sweep.ExpireAsync(found, later, CancellationToken));

        // Still sent, and still there
        var stored = await LoadParcelAsync(id);

        Assert.Equal((ParcelStatus.Uploaded, Decode(packet.Id)), (stored?.Status, stored?.AttachedPacketId));

        using (var download = await alice.DownloadParcelAsync(parcel.ParcelId, CancellationToken))
            Assert.Equal(HttpStatusCode.OK, download.StatusCode);

        // The session goes on to sweep normally, and the sent file is no longer one it takes
        Assert.DoesNotContain(await sweep.FindExpirableAsync(later, CancellationToken), candidate => candidate.Id == id);

        await SweepAsync(later);

        Assert.Equal([packet], (await alice.ListAsync(null, CancellationToken)).Packets);
        Assert.NotNull(await ReadBlobAsync(ParcelKeys.Original(id)));
    }

    [Fact]
    public async Task The_orphan_scan_removes_blobs_without_a_parcel_and_releases_parcels_whose_packet_is_gone()
    {
        using var alice = fixture.Api.CreateClientFor(PasselApiFixture.User("alice"));

        // Bytes with no parcel, as a crash between storing them and recording the parcel would leave
        var stray = Guid.CreateVersion7();
        const string foreign = "not-a-parcel";

        foreach (var key in new[] { ParcelKeys.Original(stray), ParcelKeys.Thumbnail(stray), foreign })
            await Store.PutAsync(key, new MemoryStream(RandomBytes(10)), "application/octet-stream", CancellationToken);

        // A sent file whose packet went without it, as a delete that skipped the parcel would leave it
        var parcel = await alice.UploadAsync(TestImages.Png(600, 300), "left.png", "image/png", CancellationToken);
        var packet = await alice.SendFileAsync(parcel.ParcelId, null, CancellationToken);
        var id = Decode(parcel.ParcelId);

        await using (var session = fixture.Api.Services.GetRequiredService<IDocumentStore>().LightweightSession())
        {
            session.Delete<Packet>(Decode(packet.Id));
            await session.SaveChangesAsync(CancellationToken);
        }

        using (var dangling = await alice.DownloadParcelAsync(parcel.ParcelId, CancellationToken))
            Assert.Equal(HttpStatusCode.OK, dangling.StatusCode);

        ParcelOrphanResult result;

        await using (var scope = fixture.Api.Services.CreateAsyncScope())
            result = await scope
                .ServiceProvider
                .GetRequiredService<ParcelSweep>()
                .ScanOrphansAsync(DateTimeOffset.UtcNow + TimeSpan.FromHours(3), CancellationToken);

        Assert.True(result.DanglingReleased >= 1, $"{result}");
        Assert.True(result.BlobsDeleted >= 2, $"{result}");

        Assert.Null(await Store.OpenReadAsync(ParcelKeys.Original(stray), CancellationToken));
        Assert.Null(await Store.OpenReadAsync(ParcelKeys.Thumbnail(stray), CancellationToken));

        // A name the API never writes is not the scan's to delete
        Assert.NotNull(await ReadBlobAsync(foreign));

        // The released file is gone at once, and its bytes with the next sweep
        using (var released = await alice.DownloadParcelAsync(parcel.ParcelId, CancellationToken))
            Assert.Equal(HttpStatusCode.NotFound, released.StatusCode);

        Assert.Equal(ParcelStatus.Deleted, (await LoadParcelAsync(id))?.Status);

        await SweepAsync(DateTimeOffset.UtcNow);

        Assert.Null(await Store.OpenReadAsync(ParcelKeys.Original(id), CancellationToken));
        Assert.Null(await LoadParcelAsync(id));

        // Bytes younger than the minimum age may be an upload in flight, and are left alone
        var young = ParcelKeys.Original(Guid.CreateVersion7());
        await Store.PutAsync(young, new MemoryStream(RandomBytes(10)), "application/octet-stream", CancellationToken);

        await using (var scope = fixture.Api.Services.CreateAsyncScope())
            await scope
                .ServiceProvider
                .GetRequiredService<ParcelSweep>()
                .ScanOrphansAsync(DateTimeOffset.UtcNow, CancellationToken);

        Assert.NotNull(await ReadBlobAsync(young));

        foreach (var key in new[] { foreign, young })
            await Store.DeleteAsync(key, CancellationToken);
    }

    [Fact]
    public async Task Storage_being_down_answers_503_for_files_while_texts_still_work()
    {
        var connectionString = await fixture.CreateEmptyDatabaseAsync("passel_parcels_down", CancellationToken);
        var user = PasselApiFixture.User("alice");

        await using var api = new PasselApiFactory(connectionString, parcelStore: new FailingParcelStore());
        using var alice = api.CreateClientFor(user);

        using (var upload = await alice.PostParcelAsync(TestImages.Png(16, 16), "a.png", "image/png", CancellationToken))
        {
            Assert.Equal(("File storage unavailable", "Files can't be stored or fetched right now. Try again shortly."),
                await ReadProblemAsync(upload, HttpStatusCode.ServiceUnavailable));
            Assert.Equal(TimeSpan.FromSeconds(30), upload.Headers.RetryAfter?.Delta);
        }

        // The failed upload holds nothing against the user
        Assert.Equal(new ParcelQuotaUsage(0, 0),
            await api.Services.GetRequiredService<IParcelQuotaService>().GetUsageAsync(user, CancellationToken));

        // A file recorded before the storage went down cannot be fetched either, and its bytes wait to be erased
        var uploaded = NewParcel(user, ParcelStatus.Uploaded);
        var deleted = NewParcel(user, ParcelStatus.Deleted);

        await using (var session = api.Services.GetRequiredService<IDocumentStore>().LightweightSession())
        {
            session.Insert(uploaded, deleted);
            await session.SaveChangesAsync(CancellationToken);
        }

        var encoding = api.Services.GetRequiredService<IIdEncodingService>();

        using (var download = await alice.DownloadParcelAsync(encoding.Encode(uploaded.Id), CancellationToken))
            Assert.Equal(HttpStatusCode.ServiceUnavailable, download.StatusCode);

        await using (var scope = api.Services.CreateAsyncScope())
        {
            var result = await scope
                .ServiceProvider
                .GetRequiredService<ParcelSweep>()
                .RunOnceAsync(DateTimeOffset.UtcNow, CancellationToken);

            Assert.Equal((0, 0), (result.Expired, result.Erased));
            Assert.True(result.Failed >= 1, $"{result}");

            await using var check = api.Services.GetRequiredService<IDocumentStore>().QuerySession();

            Assert.NotNull(await check.LoadAsync<Parcel>(deleted.Id, CancellationToken));
        }

        // Everything that is not a file works as ever, the health check included
        var text = await alice.CreateTextAsync(null, "still here", CancellationToken);

        Assert.Equal([text], (await alice.ListAsync(null, CancellationToken)).Packets);

        using var health = await alice.GetAsync("/health", CancellationToken);

        Assert.Equal("Healthy", await health.Content.ReadAsStringAsync(CancellationToken));
    }

    [Fact]
    public async Task With_the_sweep_on_a_deleted_file_is_erased_within_seconds()
    {
        var connectionString = await fixture.CreateEmptyDatabaseAsync("passel_parcels_sweep", CancellationToken);
        var store = new InMemoryParcelStore();

        await using var api = new PasselApiFactory(connectionString,
            settings: new Dictionary<string, string?>
            {
                ["Parcels:SweepEnabled"] = "true",
            },
            parcelStore: store);
        using var alice = api.CreateClientFor(PasselApiFixture.User("alice"));
        var encoding = api.Services.GetRequiredService<IIdEncodingService>();

        var sent = await alice.UploadAsync(TestImages.Png(600, 300), "sent.png", "image/png", CancellationToken);
        var packet = await alice.SendFileAsync(sent.ParcelId, null, CancellationToken);
        var discarded = await alice.UploadAsync(RandomBytes(10), "mistake.bin", "application/octet-stream",
            CancellationToken);

        var keys = new[] { sent, discarded }
            .Select(parcel => encoding.TryDecode(parcel.ParcelId, out var id)
                ? id
                : throw new InvalidOperationException($"{parcel.ParcelId} is not an id."))
            .SelectMany(id => new[] { ParcelKeys.Original(id), ParcelKeys.Thumbnail(id) })
            .ToList();

        // The image's original and thumbnail, and the other file's original
        Assert.Equal(3, await CountAsync(store, keys));

        // The sweep runs every five minutes; only the deletes' pokes can make it run within the test
        Assert.Equal(HttpStatusCode.NoContent, await alice.DeletePacketAsync(packet.Id, CancellationToken));
        Assert.Equal(HttpStatusCode.NoContent, await alice.DiscardParcelAsync(discarded.ParcelId, CancellationToken));

        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(10);

        while (await CountAsync(store, keys) > 0)
        {
            Assert.True(DateTimeOffset.UtcNow < deadline, "The deleted files were not erased within 10 seconds.");

            await Task.Delay(50, CancellationToken);
        }
    }

    private static async Task<int> CountAsync(IParcelStore store, IEnumerable<string> keys)
    {
        var count = 0;

        foreach (var key in keys)
            if (await store.OpenReadAsync(key, CancellationToken) is not null)
                count++;

        return count;
    }

    private static Parcel NewParcel(string userId, ParcelStatus status) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            UserId = userId,
            CreatedAt = DateTimeOffset.UtcNow,
            Status = status,
            Size = 10,
            ContentType = "application/octet-stream",
            FileName = "a.bin",
            Sha256 = status == ParcelStatus.Uploading
                ? null
                : new string('0', 64),
        };
}
