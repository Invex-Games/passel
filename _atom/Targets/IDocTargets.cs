namespace Atom.Targets;

internal interface IDocTargets : IDocFxHelper, IGithubHelper
{
    Target BuildDocs =>
        t => t
            .DescribedAs("Builds the DocFX documentation site")
            .ProducesArtifact(GeneratedDocsArtifactName)
            .Executes(cancellationToken => BuildDocFxDocs(cancellationToken: cancellationToken));

    Target ServeDocs =>
        t => t
            .DescribedAs("Builds the documentation and serves it at http://localhost:8080")
            .DependsOn(nameof(BuildDocs))
            .Executes(ServeDocFxDocs);

    // Force-pushes the built site to an orphan gh-pages branch; GitHub Pages has to be pointed at that branch once
    Target PublishDocs =>
        t => t
            .DescribedAs("Publishes the built documentation site to the gh-pages branch for GitHub Pages")
            .ConsumesArtifact(nameof(BuildDocs), GeneratedDocsArtifactName)
            .RequiresParam(nameof(GithubToken))
            .Executes(cancellationToken =>
                PublishDocFxDocsToGithub(GithubToken, GeneratedDocsArtifactName, cancellationToken));
}
