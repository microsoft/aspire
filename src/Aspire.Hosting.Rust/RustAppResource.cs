// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Rust;

/// <summary>
/// Represents a Rust application resource in the distributed application model.
/// </summary>
/// <param name="name">The name of the resource in the application model.</param>
/// <param name="workingDirectory">The working directory for the Rust application.</param>
[AspireExport(ExposeProperties = true)]
public class RustAppResource(string name, string workingDirectory)
    : ExecutableResource(name, "cargo", workingDirectory), IResourceWithServiceDiscovery, IContainerFilesDestinationResource
{
    /// <summary>
    /// The cargo arguments produced the last time the resource's command line was built.
    /// </summary>
    /// <remarks>
    /// DCP resolves a resource's arguments before it asks for the debug launch configuration
    /// (see <c>ExecutableCreator.CreateObjectAsync</c>), so the launch configuration reuses this
    /// snapshot instead of running the user's cargo argument callbacks a second time. Running them
    /// twice would break callbacks that are one-shot or that do not return the same value each call.
    /// </remarks>
    internal IReadOnlyList<string>? ResolvedCargoArgs { get; set; }
}
