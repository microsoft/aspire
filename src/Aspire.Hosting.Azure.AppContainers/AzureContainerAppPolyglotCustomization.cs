// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Azure;
using Azure.Provisioning.AppContainers;

namespace Aspire.Hosting;

internal static class AzureContainerAppPolyglotCustomization
{
    /// <summary>
    /// Configures supported Azure Container App scale settings.
    /// </summary>
    [AspireExport("configureContainerAppScale", MethodName = "configureScale")]
    internal static void ConfigureScale(ContainerApp containerApp, AzureContainerAppScaleConfig scale)
    {
        ArgumentNullException.ThrowIfNull(containerApp);
        ArgumentNullException.ThrowIfNull(scale);

        containerApp.Template ??= new ContainerAppTemplate();
        containerApp.Template.Scale ??= new ContainerAppScale();
        ApplyScale(containerApp.Template.Scale, scale);
    }

    private static void ApplyScale(ContainerAppScale target, AzureContainerAppScaleConfig source)
    {
        if (source.MinReplicas is { } minReplicas)
        {
            target.MinReplicas = minReplicas;
        }
    }
}
