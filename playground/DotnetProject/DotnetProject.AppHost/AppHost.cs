var builder = DistributedApplication.CreateBuilder(args);

var apiservice = builder.AddDotnetProject("apiservice", "../DotnetProject.ApiService")
    .WithExternalHttpEndpoints();

builder.AddDotnetProject("workerservice", "../DotnetProject.WorkerService")
    .WithReference(apiservice)
    .WithExternalHttpEndpoints();

builder.AddDotnetProject("worker", "../worker/worker.cs")
    .WithReference(apiservice)
    .WithExternalHttpEndpoints();

builder.Build().Run();
