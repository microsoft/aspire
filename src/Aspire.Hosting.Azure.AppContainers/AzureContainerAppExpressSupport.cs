// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Azure.Provisioning;
using Azure.Provisioning.AppContainers;
using Azure.Provisioning.Primitives;

namespace Aspire.Hosting.Azure.AppContainers;

internal static class AzureContainerAppExpressSupport
{
    internal const string ResourceVersion = "2026-03-02-preview";

    internal static void ConfigureEnvironment(ContainerAppManagedEnvironment environment)
    {
        environment.ResourceVersion = ResourceVersion;

        // Replace this shim when Azure.Provisioning.AppContainers exposes EnvironmentMode.
        // Express is an environment property, not a kind or workload profile:
        // https://github.com/Azure/azure-rest-api-specs/blob/main/specification/app/resource-manager/Microsoft.App/ContainerApps/preview/2026-03-02-preview/openapi.json
        var mode = new BicepValue<string>("Express");
        ((IBicepValue)mode).Self = new BicepValueReference(environment, "EnvironmentMode", ["properties", "environmentMode"]);
        environment.ProvisionableProperties["EnvironmentMode"] = mode;
    }
}
