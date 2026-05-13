namespace Atom.Targets;

internal interface IBuildWebApi : IDotnetPublishHelper
{
    Target BuildWebApi =>
        t => t
            .DescribedAs("Builds the Web API project")
            .ProducesArtifact(Projects.Invex_Passel_WebApi.Name)
            .Executes(async () => await DotnetPublishAndStage(Projects.Invex_Passel_WebApi.Name));
}
