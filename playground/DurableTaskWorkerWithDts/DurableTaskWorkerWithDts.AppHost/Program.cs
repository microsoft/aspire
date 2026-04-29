var builder = DistributedApplication.CreateBuilder(args);

var scheduler = builder.AddDurableTaskScheduler("scheduler")
    .RunAsEmulator();

var taskHub = scheduler.AddTaskHub("taskhub");

builder.AddProject<Projects.DurableTaskWorkerWithDts_Worker>("worker")
    .WithReference(taskHub)
    .WaitFor(taskHub);

builder.Build().Run();
