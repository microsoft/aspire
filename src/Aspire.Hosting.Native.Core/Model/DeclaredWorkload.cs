// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Immutable;
using Aspire.Hosting.Native.Runtime;

namespace Aspire.Hosting.Native.Model;

/// <summary>Stores desired execution and lifecycle commands, not running leases or language callbacks.</summary>
internal sealed record DeclaredWorkload(WorkloadPlan Plan, SymbolicWorkloadBindings Bindings,
    ImmutableArray<DeclaredLifecycleCommand> Lifecycle)
{
    public static DeclaredWorkload Create(WorkloadPlan plan)
    {
        WorkloadDefinition.ValidateDeclaration(plan);

        return new(plan, new([], []), []);
    }

    /// <summary>Compares desired contents, not the backing-array identities of separately authored revisions.</summary>
    public static bool Equivalent(DeclaredWorkload? left, DeclaredWorkload? right)
    {
        if (left is null || right is null)
        {
            return left is null && right is null;
        }

        return EquivalentPlan(left.Plan, right.Plan) &&
            EquivalentBindings(left.Bindings, right.Bindings) &&
            left.Lifecycle.Length == right.Lifecycle.Length &&
            left.Lifecycle.Zip(right.Lifecycle).All(pair => pair.First.Phase == pair.Second.Phase &&
                EquivalentPlan(pair.First.Plan, pair.Second.Plan) && EquivalentBindings(pair.First.Bindings, pair.Second.Bindings));
    }

    private static bool EquivalentBindings(SymbolicWorkloadBindings left, SymbolicWorkloadBindings right) =>
        left.Environment.SequenceEqual(right.Environment) && left.Arguments.SequenceEqual(right.Arguments);

    private static bool EquivalentPlan(WorkloadPlan left, WorkloadPlan right) =>
        left.Environment.SequenceEqual(right.Environment) && left.Arguments.SequenceEqual(right.Arguments) &&
        left.Endpoints.SequenceEqual(right.Endpoints) && (left, right) switch
        {
            (ContainerWorkload first, ContainerWorkload second) => first.Image == second.Image && first.Command == second.Command &&
                first.Mounts.SequenceEqual(second.Mounts),
            (ExecutableWorkload first, ExecutableWorkload second) =>
                first.ExecutablePath == second.ExecutablePath && first.WorkingDirectory == second.WorkingDirectory,
            _ => false
        };
}

/// <summary>Names a portable provider-owned one-shot executable and its native late-bound input slots.</summary>
internal sealed record DeclaredLifecycleCommand(string Phase, ExecutableWorkload Plan, SymbolicWorkloadBindings Bindings);

internal static class LifecycleCommandPhases
{
    public const string Initialize = "initialize";
    public const string Health = "health";
    public const string Ready = "ready";
    public const string Retire = "retire";
}
