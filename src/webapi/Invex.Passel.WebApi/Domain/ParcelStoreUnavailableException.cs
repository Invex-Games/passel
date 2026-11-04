namespace Invex.Passel.WebApi.Domain;

/// <summary>
///     The API has no file storage to use: the host was started without a <c>passel-blobs</c> connection and without
///     <c>Parcels:Store</c> set to <c>FileSystem</c>. Answered with 503 like a storage outage, so texts and links keep
///     working while files do not.
/// </summary>
internal sealed class ParcelStoreUnavailableException()
    : Exception("No file storage is configured: neither the passel-blobs connection string nor Parcels:Store=FileSystem is set.");
