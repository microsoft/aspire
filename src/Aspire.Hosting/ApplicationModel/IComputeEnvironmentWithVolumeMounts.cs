// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Implemented by compute environments that materialize volume mounts for the workloads they host.
/// </summary>
/// <remarks>
/// <para>
/// Binding a volume to an environment variable promises the workload that reading that variable yields a
/// path backed by storage. Only a compute environment that consumes <see cref="ContainerMountAnnotation"/>
/// can keep that promise, so those environments declare the capability by implementing this interface.
/// </para>
/// <para>
/// When the target environment does not implement this interface, resolving the variable fails at publish
/// time rather than emitting a path that resolves to ordinary writable container storage. Without the
/// check the failure is silent and late: writes succeed, and the data disappears when the workload
/// restarts or is rescheduled.
/// </para>
/// </remarks>
public interface IComputeEnvironmentWithVolumeMounts : IComputeEnvironmentResource
{
}
