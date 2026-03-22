#pragma warning disable ASPIREPIPELINES001

using Aspire.Hosting.Pipelines;

var builder = DistributedApplication.CreateBuilder(args);

// A standalone step not related to deploy or publish
builder.Pipeline.AddStep("hello-world", async (context) =>
{
    var task = await context.ReportingStep
        .CreateTaskAsync("Running hello-world step", context.CancellationToken)
        .ConfigureAwait(false);

    await using (task.ConfigureAwait(false))
    {
        await Task.Delay(500, context.CancellationToken).ConfigureAwait(false);

        await task.CompleteAsync(
            "Hello world step completed",
            CompletionState.Completed,
            context.CancellationToken).ConfigureAwait(false);
    }
});

// A custom prerequisite for the deploy pipeline
builder.Pipeline.AddStep("custom-deploy-prereq", async (context) =>
{
    var task = await context.ReportingStep
        .CreateTaskAsync("Running custom deploy prerequisite", context.CancellationToken)
        .ConfigureAwait(false);

    await using (task.ConfigureAwait(false))
    {
        await Task.Delay(500, context.CancellationToken).ConfigureAwait(false);

        await task.CompleteAsync(
            "Custom deploy prerequisite completed",
            CompletionState.Completed,
            context.CancellationToken).ConfigureAwait(false);
    }
}, requiredBy: WellKnownPipelineSteps.Deploy);

// A custom prerequisite for the publish pipeline
builder.Pipeline.AddStep("custom-publish-prereq", async (context) =>
{
    var task = await context.ReportingStep
        .CreateTaskAsync("Running custom publish prerequisite", context.CancellationToken)
        .ConfigureAwait(false);

    await using (task.ConfigureAwait(false))
    {
        await Task.Delay(500, context.CancellationToken).ConfigureAwait(false);

        await task.CompleteAsync(
            "Custom publish prerequisite completed",
            CompletionState.Completed,
            context.CancellationToken).ConfigureAwait(false);
    }
}, requiredBy: WellKnownPipelineSteps.Publish);

builder.Build().Run();
