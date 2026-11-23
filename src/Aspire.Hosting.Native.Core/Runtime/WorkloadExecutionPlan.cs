// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Native.Model;

namespace Aspire.Hosting.Native.Runtime;

internal enum ExecutionMilestone { Allocated, Configured, Start, Ready }

/// <summary>Reports a prerequisite cycle using validated resource names and execution milestones only.</summary>
internal sealed class ExecutionPlanException(string cycle) : InvalidOperationException($"Execution prerequisite cycle: {cycle}");

/// <summary>Validates operation prerequisites, allowing reference cycles that do not form execution cycles.</summary>
internal static class WorkloadExecutionPlan
{
    public static void Validate(IEnumerable<ResourceSnapshot> declarations, bool allocationBeforeStart)
    {
        var resources = declarations.ToDictionary(resource => resource.Handle.ResourceId);
        var graph = new Dictionary<Step, HashSet<Step>>();
        foreach (var resource in resources.Values)
        {
            var id = resource.Handle.ResourceId;
            var allocation = new Step(id, ExecutionMilestone.Allocated);
            var configuration = new Step(id, ExecutionMilestone.Configured);
            var start = new Step(id, ExecutionMilestone.Start);
            var ready = new Step(id, ExecutionMilestone.Ready);
            Add(allocation);
            Add(ready);
            if (resource.Workload is null)
            {
                // Externally owned resources publish these milestones through
                // their execution authority; the native scheduler does not start them.
                continue;
            }
            Add(configuration);
            Add(start, configuration);
            Add(ready, start);
            if (allocationBeforeStart)
            {
                Add(configuration, allocation);
            }
            else
            {
                Add(allocation, start);
            }
            foreach (var reference in resource.SymbolicConfiguration.SelectMany(entry => entry.Value.References).Distinct())
            {
                Add(configuration, new Step(reference.ResourceId, ExecutionMilestone.Allocated));
            }
            foreach (var dependency in resource.Dependencies)
            {
                Add(start, new Step(dependency.ResourceId, ExecutionMilestone.Ready));
            }
        }

        // Iterative DFS avoids stack growth with a long dependency chain. A gray
        // node is on the current path; encountering it gives the actual cycle.
        var colors = new Dictionary<Step, int>();
        var path = new List<Step>();
        foreach (var root in graph.Keys)
        {
            var pending = new Stack<(Step Step, bool Exit)>();
            pending.Push((root, false));
            while (pending.TryPop(out var visit))
            {
                if (visit.Exit)
                {
                    colors[visit.Step] = 2;
                    path.RemoveAt(path.Count - 1);
                    continue;
                }
                var color = colors.GetValueOrDefault(visit.Step);
                if (color == 2)
                {
                    continue;
                }
                if (color == 1)
                {
                    var cycle = path.Skip(path.IndexOf(visit.Step)).Append(visit.Step);
                    throw new ExecutionPlanException(string.Join(" -> ", cycle.Select(step =>
                        $"{resources[step.ResourceId].Name}.{step.Milestone}")));
                }
                colors[visit.Step] = 1;
                path.Add(visit.Step);
                pending.Push((visit.Step, true));
                foreach (var dependency in graph[visit.Step])
                {
                    pending.Push((dependency, false));
                }
            }
        }

        void Add(Step step, params Step[] prerequisites)
        {
            if (!graph.TryGetValue(step, out var dependencies))
            {
                graph.Add(step, dependencies = []);
            }
            dependencies.UnionWith(prerequisites);
        }
    }

    private readonly record struct Step(Guid ResourceId, ExecutionMilestone Milestone);
}
