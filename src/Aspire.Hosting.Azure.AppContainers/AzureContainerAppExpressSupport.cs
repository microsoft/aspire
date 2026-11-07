// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Azure.Provisioning.AppContainers;

namespace Aspire.Hosting.Azure.AppContainers;

internal static class AzureContainerAppExpressSupport
{
    internal const string ResourceVersion = "2026-03-02-preview";

    internal static void ConfigureEnvironment(ContainerAppManagedEnvironment environment)
    {
        environment.ResourceVersion = ResourceVersion;

        environment.EnvironmentMode = ManagedEnvironmentMode.Express;
    }
}
