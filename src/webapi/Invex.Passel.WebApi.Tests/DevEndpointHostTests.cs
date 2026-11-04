using Asp.Versioning;
using Invex.Passel.WebApi.Domain;
using Invex.Passel.WebApi.Endpoints;
using Invex.Passel.WebApi.Extensions;
using Invex.Passel.WebApi.Services;
using Marten;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Matching;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Invex.Passel.WebApi.Tests;

public sealed class DevEndpointHostTests
{
    /// <summary>
    ///     Builds the Development endpoints the way <c>Program.cs</c> does, with only the services mapping them
    ///     needs (the OpenAPI services are resolved per request, so they are not among them), and returns every
    ///     endpoint they register.
    /// </summary>
    private static List<Endpoint> MapDevEndpoints()
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development,
        });

        builder
            .Services
            // The reader the API uses by default, named so the versioning analyzer (AV0015) is satisfied
            .AddApiVersioning(options => options.ApiVersionReader = new QueryStringApiVersionReader())
            .AddApiExplorer();

        var app = builder.Build();

        app.MapDevApi();

        return
        [
            ..((IEndpointRouteBuilder)app)
            .DataSources
            .SelectMany(source => source.Endpoints),
        ];
    }

    private static async Task<bool> Matches(Endpoint endpoint, string host)
    {
        var policy = new HostMatcherPolicy();
        var candidates = new CandidateSet([endpoint], [new RouteValueDictionary()], [0]);

        var httpContext = new DefaultHttpContext();
        httpContext.Request.Host = new HostString(host);

        await policy.ApplyAsync(httpContext, candidates);

        return candidates.IsValidCandidate(0);
    }

    [Fact]
    public void Every_development_endpoint_is_restricted_to_localhost()
    {
        var endpoints = MapDevEndpoints();

        // The login endpoints, the OpenAPI document and Scalar
        Assert.True(endpoints.Count >= 5, $"Expected the dev endpoints, found {endpoints.Count}");

        Assert.All(endpoints,
            endpoint =>
            {
                var hosts = endpoint.Metadata.GetMetadata<IHostMetadata>();

                Assert.NotNull(hosts);
                Assert.Equal([HostDevExtensions.DevelopmentHost], hosts.Hosts);
            });
    }

    [Theory]
    [InlineData("localhost:7030", true)]
    [InlineData("localhost:5218", true)]
    [InlineData("localhost", true)]
    [InlineData("LOCALHOST:7030", true)]
    [InlineData("passel-api.euw.devtunnels.ms", false)]
    [InlineData("passel-api.euw.devtunnels.ms:443", false)]
    [InlineData("10.0.2.2:5218", false)]
    [InlineData("127.0.0.1:7030", false)]
    public async Task The_host_restriction_accepts_localhost_on_any_port_and_nothing_else(string host, bool expected)
    {
        var scalar = MapDevEndpoints()
            .First(endpoint => endpoint.DisplayName?.Contains("scalar", StringComparison.OrdinalIgnoreCase) is true);

        Assert.Equal(expected, await Matches(scalar, host));
    }

    [Fact]
    public void The_packet_endpoints_are_not_restricted()
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development,
        });

        builder
            .Services
            // The reader the API uses by default, named so the versioning analyzer (AV0015) is satisfied
            .AddApiVersioning(options => options.ApiVersionReader = new QueryStringApiVersionReader())
            .AddApiExplorer();

        // The packet endpoints inject these. Mapping only needs them registered, since the delegates are built
        // here but never run, so none is ever resolved
        foreach (var service in new[]
                 {
                     typeof(IIdEncodingService),
                     typeof(IPacketStoreService),
                     typeof(IPacketQuotaService),
                     typeof(IPacketEventHub),
                     typeof(IQuerySession),
                 })
            builder.Services.AddSingleton(service,
                _ => throw new InvalidOperationException($"{service.Name} is not resolved while mapping"));

        var app = builder.Build();

        app.MapPacketEndpoints();

        var endpoints = ((IEndpointRouteBuilder)app)
            .DataSources
            .SelectMany(source => source.Endpoints)
            .ToList();

        Assert.NotEmpty(endpoints);
        Assert.All(endpoints, endpoint => Assert.Null(endpoint.Metadata.GetMetadata<IHostMetadata>()));
    }
}
