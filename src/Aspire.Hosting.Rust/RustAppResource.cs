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

    /// <summary>
    /// Serializes and caches the <c>cargo metadata</c> query behind the debug launch configuration.
    /// </summary>
    /// <remarks>
    /// Aspire may ask for the launch configuration several times for the same resource, and a cold
    /// <c>cargo metadata</c> can take well over ten seconds. The crate's target layout cannot change while the
    /// app host is running, so the first successful resolution is reused and concurrent producers wait on it
    /// rather than each spawning cargo. Only a successful result is cached, so a failure caused by a missing
    /// toolchain or a transient error is retried on the next launch attempt.
    /// </remarks>
    internal SemaphoreSlim DebugExecutablePathGate { get; } = new(1, 1);

    /// <summary>
    /// The debug executable path resolved by a previous launch configuration producer invocation, if any.
    /// </summary>
    internal string? ResolvedDebugExecutablePath { get; set; }
}
