// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREPIPELINES001

namespace Aspire.Hosting.Pipelines.GitHubActions;

/// <summary>
/// Resolves pipeline step scheduling onto workflow jobs, validating that step-to-job
/// assignments are consistent with the step dependency graph.
/// </summary>
internal static class SchedulingResolver
{
    /// <summary>
    /// Resolves step-to-job assignments and computes job dependencies.
    /// </summary>
    /// <param name="steps">The pipeline steps to resolve.</param>
    /// <param name="workflow">The workflow resource containing the declared jobs.</param>
    /// <returns>The resolved scheduling result.</returns>
    /// <exception cref="SchedulingValidationException">
    /// Thrown when the step-to-job assignments create circular job dependencies or are otherwise invalid.
    /// </exception>
    public static SchedulingResult Resolve(IReadOnlyList<PipelineStep> steps, GitHubActionsWorkflowResource workflow)
    {
        ArgumentNullException.ThrowIfNull(steps);
        ArgumentNullException.ThrowIfNull(workflow);

        // Build reverse dependency map: step name → steps that depend on it
        var reverseDeps = new Dictionary<string, List<PipelineStep>>(StringComparer.Ordinal);

        foreach (var step in steps)
        {
            foreach (var dep in step.DependsOnSteps)
            {
                if (!reverseDeps.TryGetValue(dep, out var list))
                {
                    list = [];
                    reverseDeps[dep] = list;
                }

                list.Add(step);
            }
        }

        // Phase 1: resolve all explicitly scheduled steps (ScheduledBy is set)
        var explicitStepToJob = new Dictionary<string, GitHubActionsJobResource>(StringComparer.Ordinal);
        var hasExplicitTargets = false;

        foreach (var step in steps)
        {
            if (step.ScheduledBy is not null)
            {
                explicitStepToJob[step.Name] = ResolveExplicitTarget(step, workflow);
                hasExplicitTargets = true;
            }
        }

        // Pre-existing jobs/stages on the workflow also count as explicit targets
        hasExplicitTargets = hasExplicitTargets || workflow.Jobs.Count > 0 || workflow.Stages.Count > 0;

        // Phase 2: resolve unscheduled steps by pulling them into the first consumer's target
        var stepToJob = new Dictionary<string, GitHubActionsJobResource>(explicitStepToJob, StringComparer.Ordinal);
        var orphanSteps = new List<PipelineStep>();

        foreach (var step in steps)
        {
            if (step.ScheduledBy is not null)
            {
                continue;
            }

            if (hasExplicitTargets)
            {
                var consumerJob = FindFirstConsumerJob(step.Name, reverseDeps, explicitStepToJob);
                if (consumerJob is not null)
                {
                    stepToJob[step.Name] = consumerJob;
                }
                else
                {
                    orphanSteps.Add(step);
                }
            }
            else
            {
                stepToJob[step.Name] = workflow.GetOrAddDefaultJob();
            }
        }

        // Phase 2b: resolve orphan steps (no consumer chain to an explicit target) by
        // co-locating them with their dependencies. This avoids creating spurious cross-job
        // dependencies that can introduce cycles in the job dependency graph.
        // Iterate until stable to handle chains of orphan-to-orphan dependencies.
        var remaining = orphanSteps;
        while (remaining.Count > 0)
        {
            var unresolved = new List<PipelineStep>();
            var progress = false;

            foreach (var step in remaining)
            {
                var depJob = FindDependencyJob(step, stepToJob);
                if (depJob is not null)
                {
                    stepToJob[step.Name] = depJob;
                    progress = true;
                }
                else
                {
                    unresolved.Add(step);
                }
            }

            if (!progress)
            {
                // No progress — assign remaining orphans to first available job
                foreach (var step in unresolved)
                {
                    stepToJob[step.Name] = GetFirstAvailableJob(workflow);
                }

                break;
            }

            remaining = unresolved;
        }

        // Build step lookup
        var stepsByName = steps.ToDictionary(s => s.Name, StringComparer.Ordinal);

        // Project step DAG onto job dependency graph
        var jobDependencies = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        foreach (var step in steps)
        {
            var currentJob = stepToJob[step.Name];

            if (!jobDependencies.ContainsKey(currentJob.Id))
            {
                jobDependencies[currentJob.Id] = [];
            }

            foreach (var depName in step.DependsOnSteps)
            {
                if (!stepToJob.TryGetValue(depName, out var depJob))
                {
                    // Dependency is not in our step list — skip (might be a well-known step)
                    continue;
                }

                if (depJob.Id != currentJob.Id)
                {
                    jobDependencies[currentJob.Id].Add(depJob.Id);
                }
            }
        }

        // Ensure all jobs are in the dependency graph
        foreach (var job in workflow.Jobs)
        {
            if (!jobDependencies.ContainsKey(job.Id))
            {
                jobDependencies[job.Id] = [];
            }
        }

        // Also include any explicitly declared job dependencies
        foreach (var job in workflow.Jobs)
        {
            foreach (var dep in job.DependsOnJobs)
            {
                jobDependencies[job.Id].Add(dep);
            }
        }

        // Validate: job dependency graph must be a DAG (detect cycles)
        ValidateNoCycles(jobDependencies);

        // Group steps by job
        var stepsPerJob = new Dictionary<string, List<PipelineStep>>(StringComparer.Ordinal);
        foreach (var step in steps)
        {
            var job = stepToJob[step.Name];
            if (!stepsPerJob.TryGetValue(job.Id, out var list))
            {
                list = [];
                stepsPerJob[job.Id] = list;
            }
            list.Add(step);
        }

        // Compute terminal steps per job: steps that no other step in the same job depends on.
        // These are the "leaf" steps whose transitive closure covers all steps in the job.
        var terminalStepsPerJob = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var (jobId, jobSteps) in stepsPerJob)
        {
            var jobStepNames = new HashSet<string>(jobSteps.Select(s => s.Name), StringComparer.Ordinal);

            // A step is a dependency within this job if any other step in the same job depends on it
            var intraJobDependencies = new HashSet<string>(StringComparer.Ordinal);
            foreach (var step in jobSteps)
            {
                foreach (var dep in step.DependsOnSteps)
                {
                    if (jobStepNames.Contains(dep))
                    {
                        intraJobDependencies.Add(dep);
                    }
                }
            }

            // Terminal steps are those NOT depended on by any other step in the same job
            var terminals = jobSteps
                .Where(s => !intraJobDependencies.Contains(s.Name))
                .Select(s => s.Name)
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToList();

            terminalStepsPerJob[jobId] = terminals.Count > 0 ? terminals : [jobSteps[^1].Name];
        }

        // The default job is whatever was auto-created during resolution (if any)
        GitHubActionsJobResource? defaultJob = null;
        for (var i = 0; i < workflow.Jobs.Count; i++)
        {
            if (workflow.Jobs[i].Id == "default")
            {
                defaultJob = workflow.Jobs[i];
                break;
            }
        }
        defaultJob ??= workflow.Jobs.Count > 0 ? workflow.Jobs[0] : null;

        return new SchedulingResult
        {
            StepToJob = stepToJob,
            JobDependencies = jobDependencies.ToDictionary(
                kvp => kvp.Key,
                kvp => (IReadOnlySet<string>)kvp.Value,
                StringComparer.Ordinal),
            StepsPerJob = stepsPerJob.ToDictionary(
                kvp => kvp.Key,
                kvp => (IReadOnlyList<PipelineStep>)kvp.Value,
                StringComparer.Ordinal),
            TerminalStepsPerJob = terminalStepsPerJob,
            DefaultJob = defaultJob
        };
    }

    /// <summary>
    /// Resolves the job for a step that has an explicit <see cref="PipelineStep.ScheduledBy"/> target.
    /// </summary>
    private static GitHubActionsJobResource ResolveExplicitTarget(PipelineStep step, GitHubActionsWorkflowResource workflow)
    {
        return step.ScheduledBy switch
        {
            GitHubActionsJobResource job when job.Workflow != workflow =>
                throw new SchedulingValidationException(
                    $"Step '{step.Name}' is scheduled on job '{job.Id}' from a different workflow. " +
                    $"Steps can only be scheduled on jobs within the same workflow."),

            GitHubActionsJobResource job => job,

            GitHubActionsStageResource stage when stage.Workflow != workflow =>
                throw new SchedulingValidationException(
                    $"Step '{step.Name}' is scheduled on stage '{stage.Name}' from a different workflow. " +
                    $"Steps can only be scheduled on stages within the same workflow."),

            GitHubActionsStageResource stage => stage.GetOrAddDefaultJob(),

            GitHubActionsWorkflowResource w when w != workflow =>
                throw new SchedulingValidationException(
                    $"Step '{step.Name}' is scheduled on workflow '{w.Name}' but is being resolved against workflow '{workflow.Name}'."),

            GitHubActionsWorkflowResource w => w.GetOrAddDefaultJob(),

            _ => throw new SchedulingValidationException(
                    $"Step '{step.Name}' has a ScheduledBy target of type '{step.ScheduledBy!.GetType().Name}' " +
                    $"which is not a recognized GitHub Actions target (workflow, stage, or job).")
        };
    }

    /// <summary>
    /// BFS through reverse dependencies to find the first explicitly-scheduled consumer's job.
    /// This enables unscheduled steps to be "pulled into" the target of their nearest consumer.
    /// </summary>
    private static GitHubActionsJobResource? FindFirstConsumerJob(
        string stepName,
        Dictionary<string, List<PipelineStep>> reverseDeps,
        Dictionary<string, GitHubActionsJobResource> explicitStepToJob)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal) { stepName };
        var queue = new Queue<string>();
        queue.Enqueue(stepName);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();

            if (!reverseDeps.TryGetValue(current, out var consumers))
            {
                continue;
            }

            foreach (var consumer in consumers)
            {
                if (explicitStepToJob.TryGetValue(consumer.Name, out var job))
                {
                    return job;
                }

                if (visited.Add(consumer.Name))
                {
                    queue.Enqueue(consumer.Name);
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Finds a job for an orphan step by looking at where its dependencies are assigned.
    /// This co-locates orphan steps with their dependencies to avoid creating cross-job
    /// dependencies that could introduce cycles in the job dependency graph.
    /// </summary>
    private static GitHubActionsJobResource? FindDependencyJob(
        PipelineStep step,
        Dictionary<string, GitHubActionsJobResource> stepToJob)
    {
        foreach (var depName in step.DependsOnSteps)
        {
            if (stepToJob.TryGetValue(depName, out var job))
            {
                return job;
            }
        }

        return null;
    }

    /// <summary>
    /// Gets the first available job on the workflow for orphan unscheduled steps
    /// (steps with no downstream consumer that has explicit scheduling).
    /// </summary>
    private static GitHubActionsJobResource GetFirstAvailableJob(GitHubActionsWorkflowResource workflow)
    {
        if (workflow.Stages.Count > 0)
        {
            return workflow.Stages[0].GetOrAddDefaultJob();
        }

        if (workflow.Jobs.Count > 0)
        {
            return workflow.Jobs[0];
        }

        return workflow.GetOrAddDefaultJob();
    }

    private static void ValidateNoCycles(Dictionary<string, HashSet<string>> jobDependencies)
    {
        // DFS-based cycle detection with three-state visiting
        var visited = new Dictionary<string, VisitState>(StringComparer.Ordinal);
        var cyclePath = new List<string>();

        foreach (var jobId in jobDependencies.Keys)
        {
            visited[jobId] = VisitState.Unvisited;
        }

        foreach (var jobId in jobDependencies.Keys)
        {
            if (visited[jobId] == VisitState.Unvisited)
            {
                if (HasCycleDfs(jobId, jobDependencies, visited, cyclePath))
                {
                    cyclePath.Reverse();
                    var cycleDescription = string.Join(" → ", cyclePath);
                    throw new SchedulingValidationException(
                        $"Pipeline step scheduling creates a circular dependency between jobs: {cycleDescription}. " +
                        $"This typically happens when step A depends on step B, but their job assignments " +
                        $"create a cycle in the job dependency graph.");
                }
            }
        }
    }

    private static bool HasCycleDfs(
        string jobId,
        Dictionary<string, HashSet<string>> jobDependencies,
        Dictionary<string, VisitState> visited,
        List<string> cyclePath)
    {
        visited[jobId] = VisitState.Visiting;

        if (jobDependencies.TryGetValue(jobId, out var deps))
        {
            foreach (var dep in deps)
            {
                if (!visited.TryGetValue(dep, out var state))
                {
                    continue;
                }

                if (state == VisitState.Visiting)
                {
                    cyclePath.Add(dep);
                    cyclePath.Add(jobId);
                    return true;
                }

                if (state == VisitState.Unvisited && HasCycleDfs(dep, jobDependencies, visited, cyclePath))
                {
                    cyclePath.Add(jobId);
                    return true;
                }
            }
        }

        visited[jobId] = VisitState.Visited;
        return false;
    }

    private enum VisitState
    {
        Unvisited,
        Visiting,
        Visited
    }
}

/// <summary>
/// The result of resolving pipeline step scheduling onto workflow jobs.
/// </summary>
internal sealed class SchedulingResult
{
    /// <summary>
    /// Gets the mapping of step names to their assigned jobs.
    /// </summary>
    public required Dictionary<string, GitHubActionsJobResource> StepToJob { get; init; }

    /// <summary>
    /// Gets the computed job dependency graph (job ID → set of job IDs it depends on).
    /// </summary>
    public required Dictionary<string, IReadOnlySet<string>> JobDependencies { get; init; }

    /// <summary>
    /// Gets the steps grouped by their assigned job.
    /// </summary>
    public required Dictionary<string, IReadOnlyList<PipelineStep>> StepsPerJob { get; init; }

    /// <summary>
    /// Gets the terminal step names per job. Terminal steps are those not depended on by any other
    /// step in the same job — executing them via <c>aspire do</c> covers all steps in the job.
    /// </summary>
    public required Dictionary<string, IReadOnlyList<string>> TerminalStepsPerJob { get; init; }

    /// <summary>
    /// Gets the default job used for unscheduled steps, or <c>null</c> if all steps were explicitly scheduled.
    /// </summary>
    public GitHubActionsJobResource? DefaultJob { get; init; }
}
