namespace Invex.Passel.WebApi.Endpoints.DeviceEndpoints;

public static partial class DeviceEndpoints
{
    /// <summary>
    ///     A device as the API returns it: the shape of a <c>GET /api/devices</c> item and of the registration and
    ///     rename responses.
    /// </summary>
    /// <remarks>
    ///     Built from the stored <see cref="Device" /> by <see cref="From" /> alone. It leaves out the installation id,
    ///     which the client already has, and the push token, which is a credential for reaching the device and only
    ///     ever travels from the client to the API; <see cref="PushUpdatedAt" /> says whether there is one.
    /// </remarks>
    [PublicAPI]
    public sealed record DeviceResponse
    {
        /// <summary>
        ///     The device's public id, opaque to clients. A client keeps the id its registration returned to tell
        ///     itself apart in the list.
        /// </summary>
        public required string Id { get; init; }

        public required string Name { get; init; }

        public required DevicePlatform Platform { get; init; }

        /// <summary>
        ///     When the installation first registered, in UTC.
        /// </summary>
        public required DateTimeOffset CreatedAt { get; init; }

        /// <summary>
        ///     When the installation last registered, in UTC.
        /// </summary>
        public required DateTimeOffset LastSeenAt { get; init; }

        /// <summary>
        ///     When the device's push token was last set, in UTC, or <c>null</c> when it has none.
        /// </summary>
        public required DateTimeOffset? PushUpdatedAt { get; init; }

        /// <summary>
        ///     The one mapping from a stored device to the wire. The id is a public id, never the stored Guid.
        /// </summary>
        public static DeviceResponse From(Device device, IIdEncodingService encodingService) =>
            new()
            {
                Id = encodingService.Encode(device.Id),
                Name = device.Name,
                Platform = device.Platform,
                CreatedAt = device.CreatedAt,
                LastSeenAt = device.LastSeenAt,
                PushUpdatedAt = device.Push?.UpdatedAt,
            };
    }
}
