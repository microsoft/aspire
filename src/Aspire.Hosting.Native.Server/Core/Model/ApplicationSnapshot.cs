// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Immutable;

namespace Aspire.Hosting.Native.Model;

/// <summary>Provides an immutable declaration graph without runtime execution state.</summary>
internal sealed record ApplicationSnapshot(Guid GenerationId, ImmutableArray<ResourceSnapshot> Resources);

/// <summary>Describes a resource and its readiness dependencies within a declaration graph.</summary>
internal sealed record ResourceSnapshot(ResourceHandle Handle, string Name, string TypeId,
    ImmutableArray<ResourceHandle> Dependencies, ImmutableArray<ResourceConfigurationEntry> Configuration);

internal sealed record ResourceConfigurationEntry(string Name, string Value);
