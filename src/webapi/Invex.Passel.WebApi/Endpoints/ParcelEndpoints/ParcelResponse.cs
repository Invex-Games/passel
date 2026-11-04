namespace Invex.Passel.WebApi.Endpoints.ParcelEndpoints;

public static partial class ParcelEndpoints
{
    /// <summary>
    ///     An uploaded file as the API returns it: the answer to an upload, and what a client sends the id of to
    ///     create a File packet.
    /// </summary>
    /// <remarks>
    ///     Built from the stored <see cref="Parcel" /> by <see cref="From" /> alone. The type, the size and the
    ///     dimensions are what the API found in the bytes, never what the client declared.
    /// </remarks>
    [PublicAPI]
    public sealed record ParcelResponse
    {
        /// <summary>
        ///     The file's public id, opaque to clients, in the same space as packet ids: a File packet's
        ///     <c>parcelId</c>.
        /// </summary>
        public required string ParcelId { get; init; }

        /// <summary>
        ///     The file name the client gave, made safe to store and to send back, or <c>null</c> when nothing of it
        ///     was left.
        /// </summary>
        public required string? FileName { get; init; }

        /// <summary>
        ///     The type sniffed from the bytes: <c>image/jpeg</c>, <c>image/png</c>, <c>image/gif</c>,
        ///     <c>image/webp</c>, <c>application/pdf</c>, or <c>application/octet-stream</c> for anything else.
        /// </summary>
        public required string ContentType { get; init; }

        /// <summary>
        ///     The file's size in bytes.
        /// </summary>
        public required long Size { get; init; }

        /// <summary>
        ///     Whether the file decoded as an image, in which case it has a thumbnail and is shown inline.
        /// </summary>
        public required bool IsImage { get; init; }

        /// <summary>
        ///     The image's width as displayed, after its EXIF orientation; <c>null</c> when it is not an image.
        /// </summary>
        public required int? Width { get; init; }

        /// <summary>
        ///     The image's height as displayed, after its EXIF orientation; <c>null</c> when it is not an image.
        /// </summary>
        public required int? Height { get; init; }

        /// <summary>
        ///     The one mapping from a stored parcel to the wire. The id is a public id, never the stored Guid.
        /// </summary>
        internal static ParcelResponse From(Parcel parcel, IIdEncodingService encodingService) =>
            new()
            {
                ParcelId = encodingService.Encode(parcel.Id),
                FileName = parcel.FileName,
                ContentType = parcel.ContentType,
                Size = parcel.Size,
                IsImage = parcel.IsImage,
                Width = parcel.Width,
                Height = parcel.Height,
            };
    }
}
