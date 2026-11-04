// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Aspire.Hosting.Pipelines;

/// <summary>
/// Extensions for detecting stale generated pipeline output.
/// </summary>
[Experimental("ASPIREPIPELINES005", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public static class PipelineOutputDiffingExtensions
{
    private const string BaselineTag = "output-diff-baseline";

    /// <summary>
    /// Adds an opt-in target that checks for stale generated pipeline output.
    /// </summary>
    /// <param name="builder">The <see cref="IDistributedApplicationBuilder"/>.</param>
    /// <param name="targetStep">The step whose completed output is compared. Defaults to <c>publish</c>.</param>
    /// <param name="diffStepName">The comparison target name. When null, uses the target name followed by <c>-and-diff</c>.</param>
    /// <param name="description">An optional description of the comparison target.</param>
    /// <returns>The <see cref="IDistributedApplicationBuilder"/> for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="builder"/> or <paramref name="targetStep"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="targetStep"/> or <paramref name="diffStepName"/> is empty.</exception>
    /// <inheritdoc cref="WithOutputDiffing(IDistributedApplicationBuilder, Func{PipelineStep})" path="/remarks" />
    /// <example>
    /// <code>
    /// builder.WithOutputDiffing();
    /// // Run: aspire do publish-and-diff
    /// </code>
    /// </example>
    [Experimental("ASPIREPIPELINES005", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    [AspireExport]
    public static IDistributedApplicationBuilder WithOutputDiffing(
        this IDistributedApplicationBuilder builder,
        string targetStep = WellKnownPipelineSteps.Publish,
        string? diffStepName = null,
        string? description = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(targetStep);
        diffStepName ??= targetStep + "-and-diff";
        ArgumentException.ThrowIfNullOrEmpty(diffStepName);

        return builder.WithOutputDiffing(() => new PipelineStep
        {
            Name = diffStepName,
            Description = description,
            DependsOnSteps = [targetStep],
            Action = _ => Task.CompletedTask
        });
    }

    /// <summary>
    /// Adds an opt-in target that checks for stale generated pipeline output.
    /// </summary>
    /// <param name="builder">The <see cref="IDistributedApplicationBuilder"/>.</param>
    /// <param name="createStep">A factory that constructs the comparison step, including its dependencies and action.</param>
    /// <returns>The <see cref="IDistributedApplicationBuilder"/> for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="builder"/> or <paramref name="createStep"/> is null.</exception>
    /// <remarks>
    /// The check compares existing output with artifacts regenerated from the current source and fails if selected output differs.
    /// Capture runs only when the comparison target is selected, directly or through another target's dependencies.
    /// The baseline captures before every selected step so prerequisite and build writes are checked.
    /// The comparison step's dependencies and action finish before the second capture and comparison.
    /// Each capture resolves the global output directory;
    /// generators should write checked artifacts beneath it. This boundary does not enforce where writes occur.
    /// Step factories and configuration callbacks run before the baseline and must not write checked artifacts.
    /// Final actions appended to the comparison step after registration and AfterPublishEvent run too late
    /// for their writes to be checked.
    /// </remarks>
    /// <example>
    /// <code>
    /// builder.WithOutputDiffing(() => new PipelineStep
    /// {
    ///     Name = "check-generated-files",
    ///     DependsOnSteps = [WellKnownPipelineSteps.Publish],
    ///     Tags = ["ci"],
    ///     Action = _ => Task.CompletedTask
    /// });
    /// // Run: aspire do check-generated-files
    /// </code>
    /// </example>
    [Experimental("ASPIREPIPELINES005", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    [AspireExportIgnore(Reason = "Polyglot callbacks can't construct and return PipelineStep instances. Use the parameter-based overload instead.")]
    public static IDistributedApplicationBuilder WithOutputDiffing(
        this IDistributedApplicationBuilder builder,
        Func<PipelineStep> createStep)
    {
        return builder.WithOutputDiffing<PipelineOutputSnapshot, DefaultPipelineOutputSnapshotter, DefaultPipelineOutputComparer>(createStep);
    }

    /// <summary>
    /// Adds an opt-in target using a typed snapshotter and comparer.
    /// </summary>
    /// <typeparam name="TSnapshot">The snapshot representation shared by capture and comparison.</typeparam>
    /// <typeparam name="TSnapshotter">The snapshotter implementation registered when none exists for <typeparamref name="TSnapshot"/>.</typeparam>
    /// <typeparam name="TComparer">The comparer implementation registered when none exists for <typeparamref name="TSnapshot"/>.</typeparam>
    /// <param name="builder">The <see cref="IDistributedApplicationBuilder"/>.</param>
    /// <param name="targetStep">The step whose completed output is compared. Defaults to <c>publish</c>.</param>
    /// <param name="diffStepName">The comparison target name. When null, uses the target name followed by <c>-and-diff</c>.</param>
    /// <param name="description">An optional description of the comparison target.</param>
    /// <returns>The <see cref="IDistributedApplicationBuilder"/> for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="builder"/> or <paramref name="targetStep"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="targetStep"/> or <paramref name="diffStepName"/> is empty.</exception>
    /// <inheritdoc cref="WithOutputDiffing{TSnapshot, TSnapshotter, TComparer}(IDistributedApplicationBuilder, Func{PipelineStep})" path="/remarks" />
    /// <example>
    /// <code>
    /// builder.WithOutputDiffing&lt;DeploymentSnapshot, DeploymentSnapshotter, DeploymentComparer&gt;();
    /// </code>
    /// </example>
    [Experimental("ASPIREPIPELINES005", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    [AspireExportIgnore(Reason = "Custom output providers use .NET snapshot and service types.")]
    public static IDistributedApplicationBuilder WithOutputDiffing<TSnapshot, TSnapshotter, TComparer>(
        this IDistributedApplicationBuilder builder,
        string targetStep = WellKnownPipelineSteps.Publish,
        string? diffStepName = null,
        string? description = null)
        where TSnapshot : notnull
        where TSnapshotter : class, IPipelineOutputSnapshotter<TSnapshot>
        where TComparer : class, IPipelineOutputComparer<TSnapshot>
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(targetStep);
        diffStepName ??= targetStep + "-and-diff";
        ArgumentException.ThrowIfNullOrEmpty(diffStepName);

        return builder.WithOutputDiffing<TSnapshot, TSnapshotter, TComparer>(() => new PipelineStep
        {
            Name = diffStepName,
            Description = description,
            DependsOnSteps = [targetStep],
            Action = _ => Task.CompletedTask
        });
    }

    /// <summary>
    /// Adds an opt-in target using a typed snapshotter and comparer.
    /// </summary>
    /// <typeparam name="TSnapshot">The snapshot representation shared by capture and comparison.</typeparam>
    /// <typeparam name="TSnapshotter">The snapshotter implementation registered when none exists for <typeparamref name="TSnapshot"/>.</typeparam>
    /// <typeparam name="TComparer">The comparer implementation registered when none exists for <typeparamref name="TSnapshot"/>.</typeparam>
    /// <param name="builder">The <see cref="IDistributedApplicationBuilder"/>.</param>
    /// <param name="createStep">A factory that constructs the comparison step, including its dependencies and action.</param>
    /// <returns>The <see cref="IDistributedApplicationBuilder"/> for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="builder"/> or <paramref name="createStep"/> is null.</exception>
    /// <inheritdoc cref="WithOutputDiffing(IDistributedApplicationBuilder, Func{PipelineStep})" path="/remarks" />
    /// <remarks>
    /// The snapshotter and comparer are registered as transient defaults for <typeparamref name="TSnapshot"/>.
    /// Existing registrations for either closed generic interface are preserved.
    /// Each capture resolves its snapshotter. A comparer is resolved after the second capture.
    /// </remarks>
    [Experimental("ASPIREPIPELINES005", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    [AspireExportIgnore(Reason = "Custom output providers use .NET snapshot and service types.")]
    public static IDistributedApplicationBuilder WithOutputDiffing<TSnapshot, TSnapshotter, TComparer>(
        this IDistributedApplicationBuilder builder,
        Func<PipelineStep> createStep)
        where TSnapshot : notnull
        where TSnapshotter : class, IPipelineOutputSnapshotter<TSnapshot>
        where TComparer : class, IPipelineOutputComparer<TSnapshot>
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(createStep);
        var step = createStep();
        // Bind once so multiple checks do not append the same configuration rules repeatedly.
        if (!builder.Services.Any(descriptor => !descriptor.IsKeyedService &&
            descriptor.ServiceType == typeof(IValidateOptions<PipelineOutputDiffOptions>) &&
            descriptor.ImplementationType == typeof(PipelineOutputDiffOptionsValidator)))
        {
            builder.Services.Configure<PipelineOutputDiffOptions>(builder.Configuration.GetSection("Pipeline:OutputDiff"));
            builder.Services.AddSingleton<IValidateOptions<PipelineOutputDiffOptions>, PipelineOutputDiffOptionsValidator>();
        }
        builder.Services.TryAddSingleton<PipelineOutputSnapshotter>();
        builder.Services.TryAddTransient<IPipelineOutputSnapshotter<TSnapshot>, TSnapshotter>();
        builder.Services.TryAddTransient<IPipelineOutputComparer<TSnapshot>, TComparer>();
        var pipeline = builder.Pipeline;
        var baselineName = step.Name + "-baseline";
        StrongBox<TSnapshot>? baselineSnapshot = null;

        pipeline.AddStep(new PipelineStep
        {
            Name = baselineName,
            Description = $"Captures output before execution when '{step.Name}' is selected.",
            Tags = [BaselineTag],
            Action = async context =>
            {
                baselineSnapshot = null;
                var execution = context.PipelineContext;
                if (!execution.SelectedSteps.Any(selected => selected.Name == step.Name))
                {
                    return;
                }

                var snapshotter = context.Services.GetRequiredService<IPipelineOutputSnapshotter<TSnapshot>>();
                var snapshot = await snapshotter.CaptureAsync(GetOutputDirectory(context), context.CancellationToken).ConfigureAwait(false);
                baselineSnapshot = new(snapshot);
            }
        });

        step.AddFinalAction(async context =>
        {
            var captured = baselineSnapshot ?? throw new InvalidOperationException($"Output diff baseline '{baselineName}' did not complete for '{step.Name}'.");
            var before = captured.Value!;
            baselineSnapshot = null;

            var snapshotter = context.Services.GetRequiredService<IPipelineOutputSnapshotter<TSnapshot>>();
            var after = await snapshotter.CaptureAsync(GetOutputDirectory(context), context.CancellationToken).ConfigureAwait(false);
            var outputComparer = context.Services.GetRequiredService<IPipelineOutputComparer<TSnapshot>>();
            var changes = await outputComparer.CompareAsync(before, after, context.CancellationToken).ConfigureAwait(false);
            if (changes.Count > 0)
            {
                var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
                var lines = changes.OrderBy(change => change.Path, comparer).ThenBy(change => change.Kind)
                    .Select(change => $"  {change.Kind}: {change.Path}");
                throw new DistributedApplicationException("Pipeline output changed:" + Environment.NewLine + string.Join(Environment.NewLine, lines));
            }
        });
        pipeline.AddStep(step);

        pipeline.AddPipelineConfiguration(context =>
        {
            var baseline = context.Steps.Single(candidate => candidate.Name == baselineName);
            foreach (var candidate in context.Steps)
            {
                // Gate collected steps, not just known publishers: later configuration can change dependencies.
                // Exclude every baseline so independently registered checks cannot gate each other in a cycle.
                if (!candidate.Tags.Contains(BaselineTag))
                {
                    baseline.RequiredBy(candidate);
                }
            }
            return Task.CompletedTask;
        });

        return builder;
    }

    private static string GetOutputDirectory(PipelineStepContext context)
    {
        var output = context.Services.GetRequiredService<IPipelineOutputService>();
        return output.GetOutputDirectory();
    }
}
