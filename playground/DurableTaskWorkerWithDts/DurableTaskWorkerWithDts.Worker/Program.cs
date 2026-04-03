using Microsoft.DurableTask.Client;
using Microsoft.DurableTask.Client.AzureManaged;
using Microsoft.DurableTask.Worker;
using Microsoft.DurableTask.Worker.AzureManaged;

var builder = Host.CreateApplicationBuilder(args);

builder.AddServiceDefaults();

var connectionString = builder.Configuration.GetConnectionString("taskhub")
    ?? throw new InvalidOperationException("Missing 'taskhub' connection string.");

builder.Services.AddDurableTaskWorker(b =>
{
    b.UseDurableTaskScheduler(connectionString);
    b.AddTasks(r =>
    {
        r.AddOrchestrator<ChainingOrchestrator>();
        r.AddActivity<SayHelloActivity>();
    });
});

builder.Services.AddDurableTaskClient(b =>
{
    b.UseDurableTaskScheduler(connectionString);
});

var host = builder.Build();
host.Run();
