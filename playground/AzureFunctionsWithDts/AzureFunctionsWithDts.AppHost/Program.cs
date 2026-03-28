var builder = DistributedApplication.CreateBuilder(args);

var storage = builder.AddAzureStorage("storage").RunAsEmulator();

var scheduler = builder.AddDurableTaskScheduler("scheduler").RunAsEmulator();

var taskHub = scheduler.AddTaskHub("taskhub");

builder.AddAzureFunctionsProject<Projects.AzureFunctionsWithDts_Functions>("funcapp")
    .WithHostStorage(storage)
    .WithEnvironment("DURABLE_TASK_SCHEDULER_CONNECTION_STRING", scheduler)
    .WithEnvironment("TASKHUB_NAME", taskHub.Resource.TaskHubName);

builder.Build().Run();
