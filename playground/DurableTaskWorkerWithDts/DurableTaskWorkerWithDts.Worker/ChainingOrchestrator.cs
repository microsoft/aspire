using Microsoft.DurableTask;

public class ChainingOrchestrator : TaskOrchestrator<object?, List<string>>
{
    public override async Task<List<string>> RunAsync(TaskOrchestrationContext context, object? input)
    {
        ILogger logger = context.CreateReplaySafeLogger<ChainingOrchestrator>();
        logger.LogInformation("Saying hello.");

        var outputs = new List<string>
        {
            await context.CallActivityAsync<string>(nameof(SayHelloActivity), "Tokyo"),
            await context.CallActivityAsync<string>(nameof(SayHelloActivity), "Seattle"),
            await context.CallActivityAsync<string>(nameof(SayHelloActivity), "London")
        };

        // returns ["Hello Tokyo!", "Hello Seattle!", "Hello London!"]
        return outputs;
    }
}
