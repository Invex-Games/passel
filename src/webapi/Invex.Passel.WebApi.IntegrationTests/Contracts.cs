using System.Text;

namespace Invex.Passel.WebApi.IntegrationTests;

/// <summary>
///     The contract files under <c>contracts/</c> at the repository root: what the running API produces, committed so
///     that the client test suites can read it and a change to the wire shape shows up as a diff. A test compares
///     what the API produces now with the committed file and fails when they differ; with
///     <see cref="UpdateVariable" /> set, it writes the file instead.
/// </summary>
internal static class Contracts
{
    /// <summary>
    ///     Set this environment variable (to anything but <c>0</c> or <c>false</c>) to have the tests rewrite the
    ///     contract files instead of checking them.
    /// </summary>
    public const string UpdateVariable = "PASSEL_UPDATE_CONTRACTS";

    public const string OpenApiDocument = "contracts/openapi.json";
    public const string FeedScenarios = "contracts/feed-scenarios.json";
    public const string FixturesDirectory = "contracts/fixtures";

    /// <summary>
    ///     A file only the repository root has, so the tests find the root from wherever the test assembly runs.
    /// </summary>
    private const string RootMarker = "Invex.Passel.slnx";

    public static readonly string RepositoryRoot = FindRepositoryRoot();

    public static bool UpdateRequested =>
        Environment.GetEnvironmentVariable(UpdateVariable) is { Length: > 0 } value &&
        value != "0" &&
        !value.Equals("false", StringComparison.OrdinalIgnoreCase);

    public static string HowToRegenerate =>
        $"Regenerate the contract files by running the integration tests with {UpdateVariable}=1 in the environment " +
        "(for example `$env:PASSEL_UPDATE_CONTRACTS = \"1\"; dotnet test --project " +
        "src/webapi/Invex.Passel.WebApi.IntegrationTests/Invex.Passel.WebApi.IntegrationTests.csproj`), then " +
        "review the diff and commit it; see docs/guides/testing.md.";

    public static string PathOf(string relativePath) =>
        Path.Combine(RepositoryRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));

    public static Task<string> ReadAsync(string relativePath, CancellationToken cancellationToken) =>
        File.ReadAllTextAsync(PathOf(relativePath), cancellationToken);

    /// <summary>
    ///     Asserts that the committed file at <paramref name="relativePath" /> holds <paramref name="generated" />,
    ///     or writes it when an update was requested. Line endings are LF in the repository; the comparison ignores
    ///     them, so a checkout with CRLF passes too.
    /// </summary>
    public static async Task AssertCurrentAsync(
        string relativePath,
        string generated,
        CancellationToken cancellationToken)
    {
        var path = PathOf(relativePath);

        if (UpdateRequested)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            await File.WriteAllTextAsync(path,
                Normalize(generated),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                cancellationToken);

            return;
        }

        if (!File.Exists(path))
            Assert.Fail($"{relativePath} is missing. {HowToRegenerate}");

        var committed = Normalize(await File.ReadAllTextAsync(path, cancellationToken));
        var expected = Normalize(generated);

        if (committed == expected)
            return;

        Assert.Fail($"{relativePath} is out of date: {FirstDifference(committed, expected)} {HowToRegenerate}");
    }

    /// <summary>
    ///     LF line endings and exactly one at the end.
    /// </summary>
    private static string Normalize(string text) =>
        text.Replace("\r\n", "\n").TrimEnd('\n') + "\n";

    private static string FirstDifference(string committed, string generated)
    {
        var committedLines = committed.Split('\n');
        var generatedLines = generated.Split('\n');

        for (var i = 0; i < Math.Min(committedLines.Length, generatedLines.Length); i++)
            if (committedLines[i] != generatedLines[i])
                return $"line {i + 1} is `{committedLines[i].Trim()}` in the committed file and " +
                       $"`{generatedLines[i].Trim()}` as generated now.";

        return $"the committed file has {committedLines.Length - 1} lines and the generated one " +
               $"{generatedLines.Length - 1}.";
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, RootMarker)))
                return directory.FullName;

        throw new InvalidOperationException(
            $"No {RootMarker} above {AppContext.BaseDirectory}; the tests must run from a checkout of the repository.");
    }
}
