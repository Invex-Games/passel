namespace Atom;

[BuildDefinition]
[GenerateEntryPoint]
[GenerateSolutionModel]
internal interface IBuild : IBuildDefinition,
    IAzureKeyVault,
    IBuildWebApi,
    IBuildMobileAppAndroid,
    IBuildChromeExtension,
    ITestTargets,
    IDocTargets,
    ISetupDevEnvironment
{
    IReadOnlyList<IBuildOption> IBuildDefinition.Options => [BuildOptions.AzureKeyVault.UseAzureKeyVault];
}
