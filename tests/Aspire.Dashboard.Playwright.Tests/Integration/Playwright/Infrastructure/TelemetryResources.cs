// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Model;
using OpenTelemetry.Proto.Common.V1;
using OpenTelemetry.Proto.Resource.V1;

namespace Aspire.Dashboard.Tests.Integration.Playwright.Infrastructure;

internal static class TelemetryResources
{
    /// <summary>
    /// Creates the OTLP resource that an app model resource with no replica suffix reports its telemetry under.
    /// </summary>
    /// <remarks>
    /// The resource pane matches an app model resource to telemetry by composite name: "{service.name}-{service.instance.id}",
    /// or just service.name when there's no instance ID (see ResourcesLayout.GetTelemetryResource). A resource whose name
    /// has no suffix, such as MockDashboardClient.TestResource1, must report only service.name. Reporting its name as the
    /// instance ID as well gives the source the key "TestResource-TestResource", which the pane lists as a separate telemetry
    /// source, so a page filtered to the app model resource shows none of its telemetry.
    /// </remarks>
    public static Resource Create(ResourceViewModel resource)
    {
        return new Resource
        {
            Attributes =
            {
                new KeyValue { Key = "service.name", Value = new AnyValue { StringValue = resource.Name } }
            }
        };
    }
}
