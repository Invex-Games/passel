namespace Atom.Helpers;

internal interface IAndroidKeystoreHelper : IProcessHelper
{
    [SecretDefinition("android-keystore-alias", "Alias for the Android keystore")]
    string? AndroidKeystoreAlias => GetParam(() => AndroidKeystoreAlias);

    [SecretDefinition("android-keystore-password", "Password for the Android keystore")]
    string AndroidKeystorePassword => GetParam(() => AndroidKeystorePassword)!;

    [SecretDefinition("android-keystore-contents", "Contents of the Android keystore in base64 format")]
    string AndroidKeystoreContents => GetParam(() => AndroidKeystoreContents)!;

    async Task<RootedPath> WriteKeystoreFile(
        string androidKeystoreContents,
        RootedPath? customPath = null,
        CancellationToken cancellationToken = default)
    {
        customPath ??= RootedFileSystem.AtomTempDirectory;
        var keystorePath = customPath / "invex.keystore";

        if (!customPath.DirectoryExists)
            RootedFileSystem.Directory.CreateDirectory(customPath);

        await RootedFileSystem.File.WriteAllBytesAsync(keystorePath,
            Convert.FromBase64String(androidKeystoreContents),
            cancellationToken);

        return keystorePath;
    }
}
