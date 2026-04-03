using Microsoft.DurableTask;

public class SayHelloActivity : TaskActivity<string, string>
{
    private readonly ILogger<SayHelloActivity> _logger;

    public SayHelloActivity(ILogger<SayHelloActivity> logger)
    {
        _logger = logger;
    }

    public override Task<string> RunAsync(TaskActivityContext context, string input)
    {
        _logger.LogInformation("Saying hello to {Name}", input);
        return Task.FromResult($"Hello {input}!");
    }
}
