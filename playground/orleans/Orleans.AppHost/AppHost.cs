var builder = DistributedApplication.CreateBuilder(args);

var storage = builder.AddAzureStorage("storage").RunAsEmulator();
var clusteringTable = storage.AddTables("clustering");
var grainStorage = storage.AddBlobs("grainstate");

var orleans = builder.AddOrleans("my-app")
                     .WithClustering(clusteringTable)
                     .WithGrainStorage("Default", grainStorage);

// For local development (see https://github.com/microsoft/aspire/issues/1823 for how to detect),
// instead of using the emulator, one can use the in memory provider from Orleans:
//
// var orleans = builder.AddOrleans("my-app")
//                      .WithDevelopmentClustering()
//                      .WithMemoryGrainStorage("Default");

builder.AddProject<Projects.OrleansServer>("silo")
       .WithReference(orleans)
       .WithReplicas(3);

builder.AddProject<Projects.OrleansClient>("frontend")
       .WithReference(orleans.AsClient())
       .WithExternalHttpEndpoints()
       .WithReplicas(3);

#if !SKIP_DASHBOARD_REFERENCE
// This project is only added in playground projects to support development/debugging
// of the dashboard. It is not required in end developer code. Comment out this code
// or build with `/p:SkipDashboardReference=true`, to test end developer
// dashboard launch experience, Refer to Directory.Build.props for the path to
// the dashboard binary (defaults to the Aspire.Dashboard bin output in the
// artifacts dir).
builder.AddProject<Projects.Aspire_Dashboard>(KnownResourceNames.AspireDashboard);
#endif

using var app = builder.Build();

await app.RunAsync();

