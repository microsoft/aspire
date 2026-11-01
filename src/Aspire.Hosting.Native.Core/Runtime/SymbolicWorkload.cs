// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Immutable;

namespace Aspire.Hosting.Native.Runtime;

internal sealed record SymbolicEnvironmentBinding(string Name, string ConfigurationKey);
internal sealed record SymbolicArgumentBinding(int Index, string ConfigurationKey);

/// <summary>Maps launch slots to server-owned resource configuration, never to caller-supplied handle identifiers.</summary>
internal sealed record SymbolicWorkloadBindings(ImmutableArray<SymbolicEnvironmentBinding> Environment,
    ImmutableArray<SymbolicArgumentBinding> Arguments)
{
    public void Validate(WorkloadPlan template)
    {
        if (Environment.IsDefault || Arguments.IsDefault || Environment.Length > 64 || Arguments.Length > 128 ||
            Environment.Any(binding => binding is null || string.IsNullOrWhiteSpace(binding.ConfigurationKey) ||
                binding.ConfigurationKey.Length > 128) ||
            Arguments.Any(binding => binding is null || string.IsNullOrWhiteSpace(binding.ConfigurationKey) ||
                binding.ConfigurationKey.Length > 128 ||
                binding.Index < 0 || binding.Index >= template.Arguments.Length) ||
            Arguments.Select(binding => binding.Index).Distinct().Count() != Arguments.Length)
        {
            throw new ArgumentException("Invalid symbolic workload bindings.");
        }
        // Validate slot names and collisions without resolving credentials or endpoints.
        WorkloadDefinition.ValidateDeclaration(Copy(template,
            template.Environment.AddRange(Environment.Select(binding => new WorkloadEnvironment(binding.Name, ""))),
            template.Arguments));
    }

    public static WorkloadPlan Copy(WorkloadPlan template, ImmutableArray<WorkloadEnvironment> environment,
        ImmutableArray<string> arguments) => template switch
        {
            ContainerWorkload container => container with { Variables = environment, Args = arguments },
            ExecutableWorkload executable => executable with { Variables = environment, Args = arguments },
            _ => throw new NotSupportedException("Unsupported symbolic workload primitive.")
        };
}
