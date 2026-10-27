var builder = DistributedApplication.CreateBuilder(args);

var keycloak = builder.AddKeycloak("keycloak", 8080)
                      .WithDataVolume()
                      .WithRealmImport("../realms");

var apiService = builder.AddDotnetProject("apiservice", @"..\Keycloak.ApiService\Keycloak.ApiService.csproj")
                        .WithReference(keycloak);

builder.AddDotnetProject("webfrontend", @"..\Keycloak.Web\Keycloak.Web.csproj")
    .WithExternalHttpEndpoints()
    .WithReference(keycloak)
    .WithReference(apiService);

builder.Build().Run();
