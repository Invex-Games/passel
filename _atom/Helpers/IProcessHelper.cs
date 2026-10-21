namespace Atom.Helpers;

internal interface IProcessHelper : IBuildAccessor
{
    async Task<RootedPath> FindEnvironmentPath(string name, CancellationToken cancellationToken = default)
    {
        var pathResult = await ProcessRunner.RunAsync(new("where", [name]), cancellationToken);

        var paths = pathResult
            .Output
            .Split(Environment.NewLine)
            .Where(x => x.Length > 0)
            .ToList();

        if (paths.Count is 0)
            throw new StepFailedException($"Failed to find '{name}' path");

        var path = Environment.OSVersion.Platform is PlatformID.Win32NT
            ? paths.FirstOrDefault(p => p.EndsWith("exe")) ??
              paths.FirstOrDefault(p => p.EndsWith("cmd")) ?? paths.FirstOrDefault(p => p.EndsWith("bat")) ?? paths[0]
            : paths.FirstOrDefault(p => p.EndsWith("sh")) ?? paths[0];

        if (!RootedFileSystem.Path.IsPathRooted(path))
            throw new StepFailedException($"'{name}' path is not rooted");

        return new(RootedFileSystem, path);
    }

    async Task<RootedPath?> TryFindEnvironmentPath(string name, CancellationToken cancellationToken = default)
    {
        try
        {
            var pathResult = await ProcessRunner.RunAsync(new("where", [name]), cancellationToken);

            var paths = pathResult
                .Output
                .Split(Environment.NewLine)
                .Where(x => x.Length > 0)
                .ToList();

            if (paths.Count is 0)
                return null;

            var path = Environment.OSVersion.Platform is PlatformID.Win32NT
                ? paths.FirstOrDefault(p => p.EndsWith("exe")) ??
                  paths.FirstOrDefault(p => p.EndsWith("cmd")) ??
                  paths.FirstOrDefault(p => p.EndsWith("bat")) ?? paths[0]
                : paths.FirstOrDefault(p => p.EndsWith("sh")) ?? paths[0];

            if (!RootedFileSystem.Path.IsPathRooted(path))
                return RootedFileSystem.CreateRootedPath(path);

            return new(RootedFileSystem, path);
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Failed to find '{Name}' path", name);

            return null;
        }
    }
}
