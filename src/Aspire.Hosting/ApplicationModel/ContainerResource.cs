// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;

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
    /// Gets or sets whether to pass custom arguments to the container entrypoint as shell command text.
    /// </summary>
    /// <remarks>
    /// When <see langword="true"/>, custom arguments are joined with spaces and passed as a single command
    /// following <c>-c</c>. Arguments are not quoted or escaped; callers must supply trusted shell command text
    /// with any necessary quoting. This property does not select a shell: <see cref="Entrypoint"/> or the image's
    /// default entrypoint must be a shell that supports <c>-c</c>, such as <c>/bin/sh</c>.
    /// When <see langword="false"/> or <see langword="null"/> (the default), arguments are passed individually
    /// without shell wrapping. This property controls local container orchestration, not published deployment output.
    /// </remarks>
    /// <example>
    /// <code>
    /// var container = builder.AddContainer("hello", "alpine")
    ///     .WithEntrypoint("/bin/sh")
    ///     .WithArgs("echo", "'hello world'");
    /// container.Resource.ShellExecution = true;
    /// </code>
    /// </example>
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
