namespace Atom.Targets;

internal interface IDocTargets : IDocFxHelper
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
}
