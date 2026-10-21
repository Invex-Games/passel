namespace Atom.Helpers;

internal interface IMobileAppHelper : IBuildAccessor
{
    RootedPath MobileAppDirectory => RootedFileSystem.AtomRootDirectory / "src" / "mobile_app";

    [ParamDefinition("mobile-app-flavor", "The android configuration")]
    MobileAppFlavor MobileAppFlavor =>
        GetParam(() => MobileAppFlavor,
            MobileAppFlavor.Development,
            converter: x => x?.ToLower() switch
            {
                "dev" or "development" => MobileAppFlavor.Development,
                "prd" or "production" => MobileAppFlavor.Production,
                _ => throw new ArgumentException($"Invalid mobile app flavor: {x}"),
            });
}

public enum MobileAppFlavor
{
    Production,
    Development,
}
