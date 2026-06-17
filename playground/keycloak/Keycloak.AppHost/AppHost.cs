var builder = DistributedApplication.CreateBuilder(args);

var keycloak = builder.AddKeycloak("keycloak", 8080)
                      .WithDataVolume()
                      .WithRealmImport("../realms");

var apiService = builder.AddProject<Projects.Keycloak_ApiService>("apiservice")
                        .WithReference(keycloak);

builder.AddProject<Projects.Keycloak_Web>("webfrontend")
    .WithExternalHttpEndpoints()
    .WithReference(keycloak)
    .WithReference(apiService);

builder.Build().Run();
