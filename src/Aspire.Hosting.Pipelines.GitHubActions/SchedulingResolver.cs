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

        var defaultJob = GetOrCreateDefaultJob(workflow);

        // Build step-to-job mapping
        var stepToJob = new Dictionary<string, GitHubActionsJobResource>(StringComparer.Ordinal);

        foreach (var step in steps)
        {
            if (step.ScheduledBy is GitHubActionsJobResource job)
            {
                if (job.Workflow != workflow)
                {
                    throw new SchedulingValidationException(
                        $"Step '{step.Name}' is scheduled on job '{job.Id}' from a different workflow. " +
                        $"Steps can only be scheduled on jobs within the same workflow.");
                }
                stepToJob[step.Name] = job;
            }
            else if (step.ScheduledBy is not null)
            {
                throw new SchedulingValidationException(
                    $"Step '{step.Name}' has a ScheduledBy target of type '{step.ScheduledBy.GetType().Name}' " +
                    $"which is not a GitHubActionsJobResource.");
            }
            else
            {
                stepToJob[step.Name] = defaultJob;
            }
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

        if (!jobDependencies.ContainsKey(defaultJob.Id))
        {
            jobDependencies[defaultJob.Id] = [];
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
            DefaultJob = defaultJob
        };
    }

    private static GitHubActionsJobResource GetOrCreateDefaultJob(GitHubActionsWorkflowResource workflow)
    {
        // If the workflow has no jobs, create a default one
        if (workflow.Jobs.Count == 0)
        {
            return workflow.AddJob("default");
        }

        // If there's exactly one job, use it as the default
        if (workflow.Jobs.Count == 1)
        {
            return workflow.Jobs[0];
        }

        // If there are multiple jobs, check if a "default" job exists
        var defaultJob = workflow.Jobs.FirstOrDefault(j => j.Id == "default");
        if (defaultJob is not null)
        {
            return defaultJob;
        }

        // Use the first job as the default
        return workflow.Jobs[0];
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
    /// Gets the default job used for unscheduled steps.
    /// </summary>
    public required GitHubActionsJobResource DefaultJob { get; init; }
}
