namespace Invex.Passel.WebApi.Endpoints.DeviceEndpoints;

public static partial class DeviceEndpoints
{
    /// <summary>
    ///     A client registering its installation. The installation id is the client's own: generated once (a random
    ///     UUID will do), kept in the client's storage and sent with every registration, so registering again finds
    ///     the same device.
    /// </summary>
    [PublicAPI]
    public sealed record RegisterDeviceRequestBody
    {
        public required string InstallationId { get; init; }

        /// <summary>
        ///     The name for a new device; a device that is registered already keeps the one it has.
        /// </summary>
        public required string Name { get; init; }

        public required DevicePlatform Platform { get; init; }
    }

    internal const int MinInstallationIdLength = 16;
    internal const int MaxInstallationIdLength = 128;
    internal const int MaxNameLength = 64;

    /// <summary>
    ///     No device request carries more than a push subscription.
    /// </summary>
    internal const long MaxRequestBodySize = 16 * 1024;

    internal static Dictionary<string, string[]> Validate(RegisterDeviceRequestBody device)
    {
        var errors = new Dictionary<string, string[]>();

        if (device.InstallationId is not { Length: >= MinInstallationIdLength and <= MaxInstallationIdLength } ||
            !device.InstallationId.All(IsInstallationIdCharacter))
            errors["installationId"] =
            [
                $"InstallationId must be {MinInstallationIdLength} to {MaxInstallationIdLength} letters, digits, '-' or '_'.",
            ];

        AddNameErrors(errors, device.Name);

        // The JSON options accept an enum's number as well as its name, so an unnamed number gets this far
        if (!Enum.IsDefined(device.Platform))
            errors["platform"] = ["Platform must be android, extension or web."];

        return errors;
    }

    /// <summary>
    ///     A name as it is stored: without the whitespace around it.
    /// </summary>
    internal static string NormalizeName(string name) =>
        name.Trim();

    private static void AddNameErrors(Dictionary<string, string[]> errors, string? name)
    {
        if (name?.Trim() is not { Length: > 0 and <= MaxNameLength } trimmed || trimmed.Any(char.IsControl))
            errors["name"] = [$"Name must be non-blank and at most {MaxNameLength} characters, without control characters."];
    }

    // A UUID in any of its usual spellings, or base64url, fits; nothing that needs escaping in a URL or a log does
    private static bool IsInstallationIdCharacter(char c) =>
        char.IsAsciiLetterOrDigit(c) || c is '-' or '_';

    public static void MapPostDevice(this IEndpointRouteBuilder builder) =>
        builder
            .MapPost(string.Empty,
                async (
                    [FromBody] RegisterDeviceRequestBody device,
                    HttpContext httpContext,
                    IIdEncodingService encodingService,
                    IDeviceStoreService deviceStoreService) =>
                {
                    var ownerId = httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier);

                    if (ownerId is not { Length: > 0 })
                        return Results.Unauthorized();

                    var errors = Validate(device);

                    if (errors.Count > 0)
                        return Results.ValidationProblem(errors);

                    var result = await deviceStoreService.Handle(new RegisterDevice
                    {
                        UserId = ownerId,
                        InstallationId = device.InstallationId,
                        Name = NormalizeName(device.Name),
                        Platform = device.Platform,
                    });

                    return result switch
                    {
                        RegisterDeviceResult.Registered registered => Results.Created(
                            $"/api/devices/{encodingService.Encode(registered.Device.Id)}",
                            DeviceResponse.From(registered.Device, encodingService)),
                        RegisterDeviceResult.Existing existing => Results.Ok(
                            DeviceResponse.From(existing.Device, encodingService)),
                        RegisterDeviceResult.PlatformMismatch mismatch => Results.Problem(
                            $"This installation is registered as a {JsonNamingPolicy.CamelCase.ConvertName(mismatch.RegisteredPlatform.ToString())} device. Use a new installation id for another platform.",
                            statusCode: StatusCodes.Status409Conflict,
                            title: "Platform mismatch"),
                        RegisterDeviceResult.LimitReached limit => Results.Problem(limit.Exceeded.Detail,
                            statusCode: limit.Exceeded.StatusCode,
                            title: limit.Exceeded.Title),
                        _ => throw new UnreachableException(),
                    };
                })
            .Produces<DeviceResponse>(StatusCodes.Status201Created)
            .Produces<DeviceResponse>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status409Conflict)
            .AcceptsJson("Register",
                new RegisterDeviceRequestBody
                {
                    InstallationId = "6f1c2a9e-4b7d-4e2a-9c3f-0d8e5b7a1c24",
                    Name = "Pixel 9",
                    Platform = DevicePlatform.Android,
                })
            .WithMetadata(new RequestSizeLimitAttribute(MaxRequestBodySize))
            .RequireRateLimiting(RateLimitPolicies.DeviceWrites);
}
