using Microsoft.DurableTask.Worker;

var builder = Host.CreateApplicationBuilder(args);

builder.AddServiceDefaults();

builder.AddDurableTaskSchedulerWorker("taskhub", worker =>
{
    worker.AddTasks(r =>
    {
        r.AddOrchestrator<ChainingOrchestrator>();
        r.AddActivity<SayHelloActivity>();
    });
});

var host = builder.Build();
host.Run();
