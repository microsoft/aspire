// <copyright file="ChaosPolicyCollectionAnnotation.cs" company="Microsoft Corporation">
// Copyright (c) Microsoft Corporation. All rights reserved.
// </copyright>

using Aspire.Chaos.Client;
using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Chaos;

/// <summary>
/// Internal annotation on the ChaosProxyResource that accumulates <see cref="ChaosPolicy"/>
/// instances installed via <c>WithPolicy(...)</c>. Each call appends; the extension method
/// re-serializes the whole list to <c>CHAOS_POLICIES_JSON</c> env var so the container sees
/// the cumulative set at startup.
/// </summary>
internal sealed class ChaosPolicyCollectionAnnotation : IResourceAnnotation
{
    public List<ChaosPolicy> Policies { get; } = new();
}
