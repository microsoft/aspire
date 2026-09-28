// <copyright file="IMeshEdgeProvider.cs" company="Microsoft Corporation">
// Copyright (c) Microsoft Corporation. All rights reserved.
// </copyright>

namespace Aspire.Hosting.Chaos;

/// <summary>
/// A pluggable mesh edge provider (R6). Each provider (i) enumerates edges of its kind from the
/// resource model and (ii) knows how to redirect the client through a chaos proxy. Keeping
/// discovery + interception behind this contract makes new resource types / protocols additive.
/// </summary>
internal interface IMeshEdgeProvider
{
    /// <summary>Gets a short provider name used in the startup summary.</summary>
    string Name { get; }

    /// <summary>
    /// Enumerates this provider's candidate edges, inserts proxies on the meshable ones, and
    /// records a <see cref="ChaosMeshEdgeReport"/> for every candidate (meshed or skipped).
    /// </summary>
    /// <param name="context">The shared mesh build context.</param>
    void Run(MeshBuildContext context);
}
