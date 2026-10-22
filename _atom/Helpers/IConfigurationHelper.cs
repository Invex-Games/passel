namespace Atom.Helpers;

internal interface IConfigurationHelper : IBuildAccessor
{
    [ParamDefinition("server-client-id", "The WebAPI client id")]
    string? ServerClientId => GetParam(() => ServerClientId);

    [ParamDefinition("mobile-app-client-id", "The Mobile App client id")]
    string? MobileAppClientId => GetParam(() => MobileAppClientId);

    [ParamDefinition("api-base-url-production", "The base URL for the production API")]
    string? ApiBaseUrlProduction => GetParam(() => ApiBaseUrlProduction);

    [ParamDefinition("api-base-url-development", "The base URL for the development API")]
    string? ApiBaseUrlDevelopment => GetParam(() => ApiBaseUrlDevelopment);
}
