// <copyright file="ChaosMeshConnectionStringOverrideAnnotation.cs" company="Microsoft Corporation">
// Copyright (c) Microsoft Corporation. All rights reserved.
// </copyright>

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Chaos;

/// <summary>
/// Marks — and owns — the chaos mesh's per-start <c>ConnectionStrings__{name}</c> proxy override on a
/// client resource (see <see cref="ConnectionStringEdgeProvider"/>). On every
/// <c>BeforeResourceStartedEvent</c> the mesh removes its prior override (this marker plus the
/// <see cref="EnvironmentCallbackAnnotation"/> it references via <see cref="OverrideCallback"/>) and
/// re-appends a fresh pair LAST, so the proxy override is the final writer of the connection string on
/// every (re)start — including a targeted <c>aspire resource rebuild</c>. Carrying the exact owned
/// callback instance lets the handler remove precisely its own annotation without disturbing the
/// client's <c>WithReference</c> callback.
/// </summary>
/// <param name="environmentVariable">The <c>ConnectionStrings__{name}</c> env var this override writes.</param>
/// <param name="overrideCallback">The mesh-owned <see cref="EnvironmentCallbackAnnotation"/> this marker
/// tracks, so it can be removed and re-appended last on each start.</param>
public sealed class ChaosMeshConnectionStringOverrideAnnotation(
    string environmentVariable,
    EnvironmentCallbackAnnotation overrideCallback) : IResourceAnnotation
{
    /// <summary>Gets the connection-string environment variable this override writes.</summary>
    public string EnvironmentVariable { get; } = environmentVariable;

    /// <summary>Gets the mesh-owned env-callback annotation this marker tracks (removed + re-appended
    /// last on each start so the proxy override always wins).</summary>
    public EnvironmentCallbackAnnotation OverrideCallback { get; } = overrideCallback;
}
