var builder = DistributedApplication.CreateBuilder(args);

var postgres = builder
    .AddPostgres("passel-pg")
    .WithPgWeb()
    .WithLifetime(ContainerLifetime.Persistent);

var postgresdb = postgres.AddDatabase("passel-db");

builder
    .AddProject<Projects.Invex_Passel_WebApi>("invex-passel-webapi")
    .WithReference(postgresdb)
    .WaitFor(postgresdb);

builder
    .Build()
    .Run();
