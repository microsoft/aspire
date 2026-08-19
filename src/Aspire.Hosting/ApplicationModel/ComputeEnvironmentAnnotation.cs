// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.ApplicationModel;

internal sealed class ComputeEnvironmentAnnotation(IComputeEnvironmentResource computeEnvironment, string? stampName = null) : IResourceAnnotation
{
    public IComputeEnvironmentResource ComputeEnvironment { get; } = computeEnvironment;

    /// <summary>
    /// Gets the explicitly configured stamp name, or <see langword="null"/> when the stamp name is derived
    /// from <see cref="ComputeEnvironment"/>.
    /// </summary>
    /// <remarks>
    /// An explicit stamp name also forces infrastructure-name qualification even for a resource bound to a
    /// single compute environment, because asking for a stamp name is an explicit request for distinct names.
    /// </remarks>
    public string? StampName { get; } = stampName;
}
