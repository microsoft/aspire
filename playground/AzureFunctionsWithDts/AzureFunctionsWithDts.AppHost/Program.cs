#pragma warning disable ASPIREDURABLETASK001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.

var builder = DistributedApplication.CreateBuilder(args);

var storage = builder.AddAzureStorage("storage").RunAsEmulator();

var scheduler = builder.AddDurableTaskScheduler("scheduler").RunAsEmulator();

var taskHub = scheduler.AddTaskHub("taskhub");

builder.AddAzureFunctionsProject<Projects.AzureFunctionsWithDts_Functions>("funcapp")
    .WithHostStorage(storage)
    .WithReference(taskHub);

builder.Build().Run();
