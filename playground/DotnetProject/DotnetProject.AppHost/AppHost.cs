var builder = DistributedApplication.CreateBuilder(args);

var apiservice = builder.AddDotnetProject("apiservice", "../DotnetProject.ApiService")
    .WithExternalHttpEndpoints();

// TODO: use WaitFor() for strict build and startup ordering between apiservice, workerservice, and worker.
// Until the coordinated .slnx build lands, the DotnetProjectResource launches each service via its own `dotnet run`, 
// so two services building the shared Playground.ServiceDefaults/SharedLibrary at the same time
// would race on the build outputs (e.g. *.deps.json). Chaining the waits guarantees only one service builds at a time.
// Remove the WaitFor() calls once the coordinated build is in place.

var workerservice = builder.AddDotnetProject("workerservice", "../DotnetProject.WorkerService")
    .WithReference(apiservice)
    .WaitFor(apiservice)
    .WithExternalHttpEndpoints();

builder.AddDotnetProject("worker", "../worker/worker.cs")
    .WithReference(apiservice)
    .WaitFor(workerservice)
    .WithExternalHttpEndpoints();

builder.Build().Run();
