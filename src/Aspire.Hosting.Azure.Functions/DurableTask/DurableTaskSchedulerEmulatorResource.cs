// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Azure.DurableTask;

/// <summary>
/// Represents the containerized emulator resource for a <see cref="DurableTaskSchedulerResource"/>.
/// This is used to host the Durable Task scheduler logic when running locally (e.g. with an Azure Functions emulator).
/// </summary>
/// <param name="scheduler">The underlying durable task scheduler resource that provides naming and annotations.</param>
/// <remarks>
/// The emulator resource delegates its annotation collection to the underlying scheduler so that configuration
/// and metadata remain consistent across both representations.
/// </remarks>
public sealed class DurableTaskSchedulerEmulatorResource(DurableTaskSchedulerResource scheduler) : ContainerResource(scheduler.Name)
{
    /// <inheritdoc />
    public override ResourceAnnotationCollection Annotations => scheduler.Annotations;
}
