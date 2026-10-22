using System.Text.Encodings.Web;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;

namespace Invex.Passel.WebApi.Authentication;

/// <summary>
///     Development-only authentication: <c>Authorization: Dev &lt;user-id&gt;</c> signs the request in as that user,
///     so the API, Scalar and curl work without Google credentials. The scheme is only registered in Development.
/// </summary>
internal sealed class DevAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "Dev";

    /// <summary>
    ///     A Google <c>sub</c> is at most 255 characters, so a development user id is too.
    /// </summary>
    internal const int MaxUserIdLength = 255;

    private const string HeaderPrefix = SchemeName + " ";

    /// <summary>
    ///     Whether the header addresses this scheme at all, whatever follows the scheme name.
    /// </summary>
    internal static bool IsDevAuthorization(StringValues authorization) =>
        SingleHeader(authorization)?.StartsWith(HeaderPrefix, StringComparison.OrdinalIgnoreCase) is true;

    /// <summary>
    ///     Reads the user id from <c>Dev &lt;user-id&gt;</c>: one to 255 ASCII letters, digits or <c>-._@+</c>, so
    ///     it can be a name, an email address or a Google-style numeric id, but never something that needs quoting.
    /// </summary>
    internal static bool TryParseUserId(StringValues authorization, [NotNullWhen(true)] out string? userId)
    {
        userId = null;

        if (SingleHeader(authorization) is not { } header ||
            !header.StartsWith(HeaderPrefix, StringComparison.OrdinalIgnoreCase))
            return false;

        var candidate = header[HeaderPrefix.Length..].Trim();

        if (candidate.Length is 0 or > MaxUserIdLength || !candidate.All(IsUserIdCharacter))
            return false;

        userId = candidate;

        return true;
    }

    private static string? SingleHeader(StringValues authorization) =>
        authorization.Count == 1
            ? authorization[0]
            : null;

    private static bool IsUserIdCharacter(char c) =>
        char.IsAsciiLetterOrDigit(c) || c is '-' or '.' or '_' or '@' or '+';

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var authorization = Request.Headers.Authorization;

        if (!IsDevAuthorization(authorization))
            return Task.FromResult(AuthenticateResult.NoResult());

        if (!TryParseUserId(authorization, out var userId))
            return Task.FromResult(AuthenticateResult.Fail(
                $"The Dev user id must be 1 to {MaxUserIdLength} ASCII letters, digits or -._@+ characters."));

        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, userId), new Claim(ClaimTypes.Name, userId)],
            Scheme.Name);

        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name);

        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
