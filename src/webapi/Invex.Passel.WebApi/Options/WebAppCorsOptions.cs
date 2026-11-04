using System.ComponentModel.DataAnnotations;

namespace Invex.Passel.WebApi.Options;

/// <summary>
///     Which browser origins may call the API across origins, bound from the <c>Cors</c> configuration section. Only
///     the web app needs it: the extension's host permissions bypass CORS and the mobile app is not a browser. Empty,
///     no origin is allowed and the API answers no request with CORS headers.
/// </summary>
internal sealed class WebAppCorsOptions : IValidatableObject
{
    public const string SectionName = "Cors";

    /// <summary>
    ///     The origins allowed, each exactly as a browser sends it in the <c>Origin</c> header: a scheme, a host and a
    ///     port only when it is not the scheme's default, with no path or trailing slash, for example
    ///     <c>https://passel.link</c> or <c>http://localhost:5245</c>.
    /// </summary>
    public string[] AllowedOrigins { get; set; } = [];

    /// <summary>
    ///     How long a browser may cache the answer to a preflight request.
    /// </summary>
    [Range(typeof(TimeSpan), "00:00:00", "02:00:00", ParseLimitsInInvariantCulture = true, ConvertValueInInvariantCulture = true)]
    public TimeSpan PreflightMaxAge { get; set; } = TimeSpan.FromMinutes(10);

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        foreach (var origin in AllowedOrigins)
            if (!IsOrigin(origin))
                yield return new($"'{origin}' is not an origin such as https://passel.link: give the scheme, the host " +
                    "and a port only when it is not the default, with no path, no trailing slash and no wildcard",
                    [nameof(AllowedOrigins)]);
    }

    /// <summary>
    ///     Whether <paramref name="value" /> is a serialized http or https origin, the form a browser compares the
    ///     <c>Origin</c> header against. Anything else would never match, or would match more than meant.
    /// </summary>
    internal static bool IsOrigin(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        uri.Scheme is "http" or "https" &&
        string.IsNullOrEmpty(uri.UserInfo) &&
        string.Equals(uri.GetLeftPart(UriPartial.Authority), value, StringComparison.OrdinalIgnoreCase);
}
