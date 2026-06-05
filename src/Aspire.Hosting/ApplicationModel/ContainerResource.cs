#pragma warning disable ASPIREPROBES001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.

// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// A resource that represents a specified container.
/// </summary>
/// <param name="name">The name of the resource.</param>
/// <param name="entrypoint">An optional container entrypoint.</param>
[DebuggerDisplay("{DebuggerToString(),nq}")]
public class ContainerResource(string name, string? entrypoint = null)
    : Resource(name), IResourceWithEnvironment, IResourceWithArgs, IResourceWithEndpoints, IResourceWithWaitSupport, IResourceWithProbes,
    IComputeResource
{
    /// <summary>
    /// The container Entrypoint.
    /// </summary>
    /// <remarks><c>null</c> means use the default Entrypoint defined by the container.</remarks>
    public string? Entrypoint { get; set; } = entrypoint;

    /// <summary>
    /// Should any custom arguments be wrapped in -c "&gt;values&lt;"?
    /// </summary>
    [Experimental("ASPIRECONTAINERSHELLEXECUTION001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    public bool? ShellExecution { get; set; }

    private string DebuggerToString()
    {
        if (!this.TryGetContainerImageName(out var imageName))
        {
            imageName = "<unknown>";
        }

        return $@"Type = {GetType().Name}, Name = ""{Name}"", Image = ""{imageName}""";
    }
}
