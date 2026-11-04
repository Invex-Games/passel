namespace Invex.Passel.WebApi.Domain;

/// <summary>
///     Where a parcel is in its life. A parcel is created <see cref="Uploading" /> before its bytes are read, becomes
///     <see cref="Uploaded" /> once they and its thumbnail are stored, and is <see cref="Deleted" /> when the upload
///     fails, the user discards it, its packet is deleted or it expires unsent. The sweep then erases its bytes and
///     the document. Whether it has been sent is not a status: it is <see cref="Parcel.AttachedPacketId" />.
/// </summary>
/// <remarks>
///     Stored as numbers (the store adds no string enum converter), so the values may not change.
/// </remarks>
[PublicAPI]
public enum ParcelStatus
{
    Uploading = 0,
    Uploaded = 1,
    Deleted = 2,
}

/// <summary>
///     An uploaded file as stored: one Marten document per upload, with the bytes in blob storage under names that
///     <c>ParcelKeys</c> derives from <see cref="Id" />. A File packet refers to it by id, and attaching it
///     to a packet sets <see cref="AttachedPacketId" />, which a unique index holds to one packet.
/// </summary>
[PublicAPI]
public sealed record Parcel
{
    /// <summary>
    ///     A version 7 <see cref="Guid" />, so id order is upload order.
    /// </summary>
    public required Guid Id { get; init; }

    public required string UserId { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public required ParcelStatus Status { get; init; }

    /// <summary>
    ///     While <see cref="ParcelStatus.Uploading" />, the bytes reserved against the user's quota (the request's
    ///     length, or the largest file allowed when it gave none); afterwards, the exact size of the file.
    /// </summary>
    public required long Size { get; init; }

    /// <summary>
    ///     The type sniffed from the bytes, never the one the client declared; <c>application/octet-stream</c> while
    ///     uploading and for anything not recognised.
    /// </summary>
    public required string ContentType { get; init; }

    /// <summary>
    ///     The file name the client gave, sanitised, or <c>null</c> when it gave none that survived. Never logged.
    /// </summary>
    public required string? FileName { get; init; }

    /// <summary>
    ///     Whether the file decoded as an image, which is what lets it be shown inline and gives it a thumbnail.
    /// </summary>
    public bool IsImage { get; init; }

    /// <summary>
    ///     The image's width as displayed, after its EXIF orientation; <c>null</c> for anything that is not an image.
    /// </summary>
    public int? Width { get; init; }

    /// <summary>
    ///     The image's height as displayed, after its EXIF orientation; <c>null</c> for anything that is not an image.
    /// </summary>
    public int? Height { get; init; }

    public bool HasThumbnail { get; init; }

    /// <summary>
    ///     The SHA-256 of the stored bytes in lower-case hex, which is also the download's entity tag; <c>null</c>
    ///     until the upload completes.
    /// </summary>
    public string? Sha256 { get; init; }

    /// <summary>
    ///     The packet the parcel was sent in, or <c>null</c> while it is unsent.
    /// </summary>
    public Guid? AttachedPacketId { get; init; }

    /// <summary>
    ///     When the parcel was marked <see cref="ParcelStatus.Deleted" />.
    /// </summary>
    public DateTimeOffset? DeletedAt { get; init; }
}
