// <copyright file="ServiceUrlBindingAnnotation.cs" company="Microsoft Corporation">
// Copyright (c) Microsoft Corporation. All rights reserved.
// </copyright>

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Records a "service URL" binding on a client resource: the client addresses
/// <see cref="Target"/> via the custom environment variable <see cref="EnvironmentVariable"/>
/// (rather than Aspire service discovery). The chaos mesh reads these annotations so a
/// service edge wired through a bespoke env var still gets routed through its proxy.
/// </summary>
/// <remarks>
/// Emitted by <c>IResourceBuilder&lt;T&gt;.WithServiceUrl(envVar, target)</c>. This is the v1
/// mechanism for the case the mesh otherwise can't see — a <c>WithEnvironment(name, target.GetEndpoint("http"))</c>
/// binding is an opaque delegate, so the mesh has no way to discover the client→target edge
/// from it. Declaring the binding via <c>WithServiceUrl</c> makes the edge explicit.
/// </remarks>
public sealed class ServiceUrlBindingAnnotation : IResourceAnnotation
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ServiceUrlBindingAnnotation"/> class.
    /// </summary>
    /// <param name="environmentVariable">The environment variable the client reads the target's URL from.</param>
    /// <param name="target">The target resource the client addresses via <paramref name="environmentVariable"/>.</param>
    public ServiceUrlBindingAnnotation(string environmentVariable, IResource target)
    {
        ArgumentException.ThrowIfNullOrEmpty(environmentVariable);
        ArgumentNullException.ThrowIfNull(target);

        this.EnvironmentVariable = environmentVariable;
        this.Target = target;
    }

    /// <summary>Gets the environment variable name the client reads the target URL from.</summary>
    public string EnvironmentVariable { get; }

    /// <summary>Gets the target resource the client addresses via the env var.</summary>
    public IResource Target { get; }
}
