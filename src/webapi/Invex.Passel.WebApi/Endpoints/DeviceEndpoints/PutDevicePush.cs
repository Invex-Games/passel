using System.Buffers.Text;

namespace Invex.Passel.WebApi.Endpoints.DeviceEndpoints;

public static partial class DeviceEndpoints
{
    /// <summary>
    ///     A device's push token: the FCM registration token of an Android installation, or the Web Push subscription
    ///     of a browser. A <c>WebPush</c> body is <c>PushSubscription.toJSON()</c> with the discriminator added; its
    ///     <c>expirationTime</c> is ignored.
    /// </summary>
    [PublicAPI]
    [Union]
    [JsonDerivedType(typeof(Fcm), typeDiscriminator: nameof(Fcm))]
    [JsonDerivedType(typeof(WebPush), typeDiscriminator: nameof(WebPush))]
    public partial record PutDevicePushRequestBody
    {
        [PublicAPI]
        public sealed partial record Fcm
        {
            public required string Token { get; init; }

            // Records print every property; the token is a credential for reaching the device
            public override string ToString() =>
                nameof(Fcm);
        }

        [PublicAPI]
        public sealed partial record WebPush
        {
            public required string Endpoint { get; init; }

            public required WebPushKeys Keys { get; init; }

            public override string ToString() =>
                nameof(WebPush);
        }
    }

    /// <summary>
    ///     The keys of a Web Push subscription, base64url as the browser gives them.
    /// </summary>
    [PublicAPI]
    public sealed record WebPushKeys
    {
        /// <summary>
        ///     The subscription's P-256 public key: 65 bytes, an uncompressed point.
        /// </summary>
        public required string P256dh { get; init; }

        /// <summary>
        ///     The subscription's authentication secret: 16 bytes.
        /// </summary>
        public required string Auth { get; init; }

        public override string ToString() =>
            nameof(WebPushKeys);
    }

    internal const int MaxFcmTokenLength = 4096;
    internal const int MaxPushEndpointLength = 2048;
    private const int P256dhLength = 65;
    private const int AuthLength = 16;

    /// <summary>
    ///     Checks the token's form. The messages never repeat the token. Whether the kind suits the device's platform
    ///     is the store's to decide, since only the stored device knows its platform.
    /// </summary>
    internal static Dictionary<string, string[]> Validate(PutDevicePushRequestBody push)
    {
        var errors = new Dictionary<string, string[]>();

        switch (push)
        {
            case PutDevicePushRequestBody.Fcm fcm:
                if (fcm.Token is not { Length: > 0 and <= MaxFcmTokenLength } || !fcm.Token.All(IsTokenCharacter))
                    errors["token"] = [$"Token must be 1 to {MaxFcmTokenLength} printable ASCII characters without spaces."];

                break;

            case PutDevicePushRequestBody.WebPush webPush:
                if (!IsPushEndpoint(webPush.Endpoint))
                    errors["endpoint"] =
                    [
                        $"Endpoint must be an absolute https URL of at most {MaxPushEndpointLength} characters, on the default port of a named host, without credentials.",
                    ];

                if (webPush.Keys is null)
                {
                    errors["keys"] = ["Keys must hold the subscription's p256dh and auth keys."];

                    break;
                }

                if (DecodeBase64Url(webPush.Keys.P256dh) is not { Length: P256dhLength } p256dh || p256dh[0] != 0x04)
                    errors["keys.p256dh"] = [$"P256dh must be an uncompressed P-256 public key of {P256dhLength} bytes, base64url."];

                if (DecodeBase64Url(webPush.Keys.Auth) is not { Length: AuthLength })
                    errors["keys.auth"] = [$"Auth must be a secret of {AuthLength} bytes, base64url."];

                break;
        }

        return errors;
    }

    /// <summary>
    ///     The token as stored, stamped with <paramref name="now" />.
    /// </summary>
    internal static DevicePush ToPush(PutDevicePushRequestBody push, DateTimeOffset now) =>
        push switch
        {
            PutDevicePushRequestBody.Fcm fcm => new DevicePush.Fcm
            {
                Token = fcm.Token,
                UpdatedAt = now,
            },
            PutDevicePushRequestBody.WebPush webPush => new DevicePush.WebPush
            {
                Endpoint = webPush.Endpoint,
                P256dh = webPush.Keys.P256dh,
                Auth = webPush.Keys.Auth,
                UpdatedAt = now,
            },
            _ => throw new UnreachableException(),
        };

    // The API will send to this URL (roadmap 2.B1), so only what a push service looks like is accepted: https to a
    // host name on the default port. The sender still has to check the addresses the name resolves to
    private static bool IsPushEndpoint(string? endpoint) =>
        endpoint is { Length: > 0 and <= MaxPushEndpointLength } &&
        Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) &&
        uri.Scheme == Uri.UriSchemeHttps &&
        uri.HostNameType == UriHostNameType.Dns &&
        uri.IsDefaultPort &&
        uri.UserInfo.Length == 0;

    private static bool IsTokenCharacter(char c) =>
        c is > ' ' and <= '~';

    private static byte[]? DecodeBase64Url(string? value) =>
        value is { Length: > 0 } && Base64Url.IsValid(value)
            ? Base64Url.DecodeFromChars(value)
            : null;

    public static void MapPutDevicePush(this IEndpointRouteBuilder builder) =>
        builder
            .MapPut("/{id}/push",
                async (
                    string id,
                    [FromBody] PutDevicePushRequestBody push,
                    HttpContext httpContext,
                    IIdEncodingService encodingService,
                    IDeviceStoreService deviceStoreService) =>
                {
                    var ownerId = httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier);

                    if (ownerId is not { Length: > 0 })
                        return Results.Unauthorized();

                    if (!encodingService.TryDecode(id, out var deviceId))
                        return Results.NotFound();

                    var errors = Validate(push);

                    if (errors.Count > 0)
                        return Results.ValidationProblem(errors);

                    var result = await deviceStoreService.Handle(new SetDevicePush
                    {
                        UserId = ownerId,
                        DeviceId = deviceId,
                        Push = ToPush(push, DateTimeOffset.UtcNow),
                    });

                    return result switch
                    {
                        SetDevicePushResult.Stored => Results.NoContent(),
                        SetDevicePushResult.NotFound => Results.NotFound(),
                        SetDevicePushResult.PlatformMismatch => Results.ValidationProblem(new Dictionary<string, string[]>
                        {
                            ["$type"] = ["An android device takes an Fcm token; an extension or web device takes a WebPush subscription."],
                        }),
                        _ => throw new UnreachableException(),
                    };
                })
            .Produces(StatusCodes.Status204NoContent)
            .ProducesValidationProblem()
            .Produces(StatusCodes.Status404NotFound)
            // Declared as the union, so the examples carry the discriminator
            .AcceptsJson<PutDevicePushRequestBody>(nameof(PutDevicePushRequestBody.Fcm),
                new PutDevicePushRequestBody.Fcm
                {
                    Token = "fcm-registration-token",
                })
            .AcceptsJson<PutDevicePushRequestBody>(nameof(PutDevicePushRequestBody.WebPush),
                new PutDevicePushRequestBody.WebPush
                {
                    Endpoint = "https://fcm.googleapis.com/fcm/send/subscription-id",
                    Keys = new()
                    {
                        P256dh = "BNcRdreALRFXTkOOUHK1EtK2wtaz5Ry4YfYCA_0QTpQtUbVlUls0VJXg7A8u-Ts1XbjhazAkj7I99e8QcYP7DkM",
                        Auth = "tBHItJI5svbpez7KI4CCXg",
                    },
                })
            .WithMetadata(new RequestSizeLimitAttribute(MaxRequestBodySize))
            .RequireRateLimiting(RateLimitPolicies.DeviceWrites);
}
