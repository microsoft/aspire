// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Azure.ServiceBus;

/// <summary>
/// Represents an annotation holding a callback that customizes the SQL Server container
/// backing the Azure Service Bus emulator.
/// </summary>
internal sealed class SqlServerContainerConfigurationAnnotation : IResourceAnnotation
{
    public SqlServerContainerConfigurationAnnotation(Action<IResourceBuilder<ContainerResource>> configure)
    {
        Configure = configure ?? throw new ArgumentNullException(nameof(configure));
    }

    public Action<IResourceBuilder<ContainerResource>> Configure { get; }
}
