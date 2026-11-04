using System.Security.Cryptography;
using System.Text;

namespace Invex.Passel.WebApi.Domain;

/// <summary>
///     The kinds of client that register as devices. A device's platform is set when it registers and decides the
///     kind of push token it may hold: an <c>Fcm</c> token on Android, a <c>WebPush</c> subscription in the extension
///     and the web app.
/// </summary>
/// <remarks>
///     The names are on the wire (camel case) and the numbers in the stored documents, so neither may change.
/// </remarks>
[PublicAPI]
public enum DevicePlatform
{
    Android = 0,
    Extension = 1,
    Web = 2,
}

/// <summary>
///     A device as stored: one Marten document per installation of a client and signed-in user. The client generates
///     the installation id once and sends it with every registration, so registering again finds the same document
///     (a unique index on the user and the installation id holds that, see <c>Program.cs</c>). The same installation
///     signed in as another account is another user's device.
/// </summary>
/// <remarks>
///     <see cref="Id" /> is what anything else refers to a device by: the sessions of roadmap package 1.B1, which a
///     removed device takes with it, and a packet's target device later. It is assigned by the API and never reused;
///     an installation whose device was removed gets a new one when it registers again.
/// </remarks>
[PublicAPI]
public sealed record Device
{
    /// <summary>
    ///     A version 7 <see cref="Guid" />, so id order is registration order.
    /// </summary>
    public required Guid Id { get; init; }

    public required string UserId { get; init; }

    /// <summary>
    ///     The client's own id for its installation, unique per user. Never returned: the client already has it.
    /// </summary>
    public required string InstallationId { get; init; }

    public required string Name { get; init; }

    public required DevicePlatform Platform { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>
    ///     When the installation last registered; clients register on every start.
    /// </summary>
    public required DateTimeOffset LastSeenAt { get; init; }

    /// <summary>
    ///     Where the push sender (roadmap package 2.B1) reaches the device, or <c>null</c> when the client has not
    ///     given a token or has withdrawn it. Set only through <see cref="WithPush" />, which keeps
    ///     <see cref="PushKey" /> in step.
    /// </summary>
    public DevicePush? Push { get; init; }

    /// <summary>
    ///     <see cref="DevicePush.KeyOf" /> of <see cref="Push" />, or <c>null</c> without one. A unique index over it
    ///     lets one push token belong to one device at a time, across all users.
    /// </summary>
    public string? PushKey { get; init; }

    /// <summary>
    ///     The device with <paramref name="push" /> as its push token, or with none.
    /// </summary>
    public Device WithPush(DevicePush? push) =>
        this with
        {
            Push = push,
            PushKey = push is null
                ? null
                : DevicePush.KeyOf(push),
        };
}

/// <summary>
///     A device's push token: a Firebase Cloud Messaging registration token, or a Web Push subscription. Stored inside
///     the device document with a <c>$type</c> discriminator; never logged and never returned.
/// </summary>
[PublicAPI]
[Union]
[JsonDerivedType(typeof(Fcm), typeDiscriminator: nameof(Fcm))]
[JsonDerivedType(typeof(WebPush), typeDiscriminator: nameof(WebPush))]
public partial record DevicePush
{
    /// <summary>
    ///     When the client last sent the token. Clients send it again on every start, so an old value marks a token
    ///     that has probably gone stale.
    /// </summary>
    public required DateTimeOffset UpdatedAt { get; init; }

    [PublicAPI]
    public sealed partial record Fcm
    {
        public required string Token { get; init; }

        // Records print every property; this one is a credential for reaching the device
        public override string ToString() =>
            $"{nameof(Fcm)} {{ {nameof(UpdatedAt)} = {UpdatedAt:O} }}";
    }

    /// <summary>
    ///     A <c>PushSubscription</c> as the browser gives it: the push service URL and the keys the payload is
    ///     encrypted with (RFC 8291).
    /// </summary>
    [PublicAPI]
    public sealed partial record WebPush
    {
        public required string Endpoint { get; init; }

        /// <summary>
        ///     The subscription's P-256 public key, base64url.
        /// </summary>
        public required string P256dh { get; init; }

        /// <summary>
        ///     The subscription's authentication secret, base64url.
        /// </summary>
        public required string Auth { get; init; }

        public override string ToString() =>
            $"{nameof(WebPush)} {{ {nameof(UpdatedAt)} = {UpdatedAt:O} }}";
    }

    /// <summary>
    ///     What identifies the push channel, whoever registered it: the SHA-256 of the FCM token or of the Web Push
    ///     endpoint, in lower-case hex. Hashed so the unique index stays small whatever the token's length, and so a
    ///     unique-violation message names a hash rather than the token.
    /// </summary>
    public static string KeyOf(DevicePush push)
    {
        var address = push switch
        {
            Fcm fcm => $"{nameof(Fcm)}:{fcm.Token}",
            WebPush webPush => $"{nameof(WebPush)}:{webPush.Endpoint}",
            _ => throw new UnreachableException(),
        };

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(address)));
    }
}
