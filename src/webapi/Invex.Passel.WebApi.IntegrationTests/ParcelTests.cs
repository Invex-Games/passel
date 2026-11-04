using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Invex.Passel.WebApi.Services;
using Invex.Passel.WebApi.Tests;
using Microsoft.Extensions.DependencyInjection;

namespace Invex.Passel.WebApi.IntegrationTests;

/// <summary>
///     Uploaded files through the real API, against a real database and Azurite: uploading, the checks on the way
///     in, the quotas, downloads and thumbnails, and discarding. The factory holds files to 1 MiB, a user to 3 MiB
///     of them and five unsent files, so every limit is cheap to reach. The tests act as users of their own, so the
///     quotas of one never see another.
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

        // Still there for its owner
        using var own = await alice.DownloadParcelAsync(parcel.ParcelId, CancellationToken);

        Assert.Equal(HttpStatusCode.OK, own.StatusCode);
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
    public async Task A_discarded_upload_cannot_be_downloaded()
    {
        var user = PasselApiFixture.User("alice");
        using var alice = fixture.Api.CreateClientFor(user);

        var parcel = await alice.UploadAsync(TestImages.Png(16, 16), "mistake.png", "image/png", CancellationToken);

        Assert.Equal(HttpStatusCode.NoContent, await alice.DiscardParcelAsync(parcel.ParcelId, CancellationToken));

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
}
