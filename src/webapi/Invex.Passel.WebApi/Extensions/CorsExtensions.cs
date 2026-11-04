using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.Extensions.Options;

namespace Invex.Passel.WebApi.Extensions;

/// <summary>
///     The one CORS policy: the web app's origins, from <see cref="WebAppCorsOptions" />, may call the packet
///     endpoints with a bearer token. Nothing else is allowed across origins.
/// </summary>
internal static class CorsExtensions
{
    public const string WebAppPolicy = "web-app";

    /// <summary>
    ///     The methods the packet endpoints use.
    /// </summary>
    internal static readonly string[] AllowedMethods = ["GET", "POST", "DELETE"];

    /// <summary>
    ///     The request headers the clients send: the token, the JSON body's type and the event stream's
    ///     <c>Accept</c>.
    /// </summary>
    internal static readonly string[] AllowedHeaders = ["Authorization", "Content-Type", "Accept"];

    /// <summary>
    ///     A 429 says when to try again; scripts can only read the headers a response exposes.
    /// </summary>
    internal static readonly string[] ExposedHeaders = ["Retry-After"];

    public static void AddWebAppCors(this WebApplicationBuilder builder)
    {
        builder
            .Services
            .AddOptions<WebAppCorsOptions>()
            .BindConfiguration(WebAppCorsOptions.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        builder.Services.AddCors();

        builder
            .Services
            .AddOptions<CorsOptions>()
            .Configure<IOptions<WebAppCorsOptions>>((cors, webApp) =>
                cors.AddPolicy(WebAppPolicy, policy => ConfigurePolicy(policy, webApp.Value)));
    }

    /// <summary>
    ///     No credentials: the clients send a bearer token, never a cookie, so a cookie (the Development login) can't
    ///     authenticate a cross-origin request either.
    /// </summary>
    internal static void ConfigurePolicy(CorsPolicyBuilder policy, WebAppCorsOptions options) =>
        policy
            .WithOrigins(options.AllowedOrigins)
            .WithMethods(AllowedMethods)
            .WithHeaders(AllowedHeaders)
            .WithExposedHeaders(ExposedHeaders)
            .SetPreflightMaxAge(options.PreflightMaxAge);

    /// <summary>
    ///     Answers preflight requests and adds the CORS headers to responses for an allowed origin. Ahead of
    ///     authentication and rate limiting, so a preflight, which carries no token, is neither refused nor counted.
    /// </summary>
    public static void UseWebAppCors(this WebApplication app) =>
        app.UseCors(WebAppPolicy);
}
