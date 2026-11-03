// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Immutable;

namespace Aspire.Hosting.Native.Model;

/// <summary>Provides an immutable declaration graph without runtime execution state.</summary>
internal sealed record ApplicationSnapshot(Guid GenerationId, ImmutableArray<ResourceSnapshot> Resources)
{
    public ImmutableArray<SymbolicValue> SecretParameters { get; init; } = [];
    public ImmutableArray<ParameterSnapshot> Parameters { get; init; } = [];
}

internal sealed record ParameterSnapshot(SymbolicValue Value, bool HasDefault);
/// <summary>Describes a resource and its readiness dependencies within a declaration graph.</summary>
internal sealed record ResourceSnapshot(ResourceHandle Handle, string Name, string TypeId,
    ImmutableArray<ResourceHandle> Dependencies, ImmutableArray<ResourceConfigurationEntry> Configuration)
{
    public ImmutableArray<SymbolicConfigurationEntry> SymbolicConfiguration { get; init; } = [];
    public DeclaredWorkload? Workload { get; init; }
    public ResourceHandle? ComputeEnvironment { get; init; }
    public bool IsComputeEnvironment { get; init; }
    public ImmutableArray<Runtime.ProviderLifecycleBinding> ProviderLifecycle { get; init; } = [];
}

internal sealed record ResourceConfigurationEntry(string Name, string Value);
