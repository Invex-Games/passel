using System.Text.RegularExpressions;
using Invex.Atom.Build.BuildInfo;

namespace Atom.Helpers;

/// <summary>
///     The tags every image of a build carries: the build's version, made safe for a tag, and the short commit hash.
///     The version tag is the one deployed and rolled back to; the commit tag traces an image to its source.
/// </summary>
internal interface IImageTagHelper : IBuildInfo
{
    /// <summary>
    ///     The pattern a tag must match before it is used. It is the OCI tag grammar without a leading underscore or
    ///     dot, and the deploy scripts on the server check the same pattern.
    /// </summary>
    static readonly Regex TagPattern = new("^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$", RegexOptions.CultureInvariant);

    /// <summary>
    ///     The build's version as an image tag. Docker tags allow neither '+' nor '/', which a version's build metadata
    ///     or a branch label can carry, so both become '-'.
    /// </summary>
    string ImageTag
    {
        get
        {
            var tag = BuildVersion
                .ToString()
                .Replace('+', '-')
                .Replace('/', '-');

            return IsValidTag(tag)
                ? tag
                : throw new StepFailedException($"The build version '{BuildVersion}' does not make a valid image tag");
        }
    }

    /// <summary>
    ///     The full hash of the commit being built: GitHub's GITHUB_SHA in a workflow, otherwise the checkout's HEAD.
    /// </summary>
    string SourceRevision
    {
        get
        {
            var revision = Environment.GetEnvironmentVariable("GITHUB_SHA");

            if (string.IsNullOrWhiteSpace(revision))
                revision = ProcessRunner
                    .Run(new("git", CommandLine.Join(["rev-parse", "HEAD"]))
                    {
                        WorkingDirectory = RootedFileSystem.AtomRootDirectory,
                        InvocationLogLevel = LogLevel.Debug,
                        OutputLogLevel = LogLevel.Debug,
                    })
                    .Output
                    .Trim();

            return revision.Length >= 7 && revision.All(char.IsAsciiHexDigit)
                ? revision.ToLowerInvariant()
                : throw new StepFailedException($"'{revision}' is not a commit hash");
        }
    }

    /// <summary>
    ///     The commit tag, <c>sha-</c> and the first seven characters of the commit hash. It also keeps two builds
    ///     apart if their version tags ever collide.
    /// </summary>
    string ShaTag => $"sha-{SourceRevision[..7]}";

    /// <summary>
    ///     Whether <paramref name="tag" /> may be used as an image tag.
    /// </summary>
    static bool IsValidTag(string? tag) => tag is not null && TagPattern.IsMatch(tag);
}
