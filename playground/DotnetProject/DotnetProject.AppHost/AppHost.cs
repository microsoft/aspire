var builder = DistributedApplication.CreateBuilder(args);

// Backend API added by path via AddDotnetProject. DotnetProjectResource is an ExecutableResource that
// launches `dotnet run --project <path>` (or `dotnet run --file <path>` for a file-based .cs app).
var apiservice = builder.AddDotnetProject("apiservice", "../DotnetProject.ApiService")
    .WithExternalHttpEndpoints();

// A second .csproj service that references the same shared library and calls the API. WithReference wires
// service discovery to apiservice; WaitFor also serializes startup so the two services don't race building
// the shared library before the coordinated build (Session 5) lands.
builder.AddDotnetProject("workerservice", "../DotnetProject.WorkerService")
    .WithReference(apiservice)
    .WaitFor(apiservice)
    .WithExternalHttpEndpoints();

// A file-based C# app (launched as `dotnet run --file worker.cs`), added by path to the .cs file. This
// dogfoods the file-based launch path of DotnetProjectResource.
builder.AddDotnetProject("worker", "../worker/worker.cs")
    .WithReference(apiservice)
    .WaitFor(apiservice)
    .WithExternalHttpEndpoints();

builder.Build().Run();
