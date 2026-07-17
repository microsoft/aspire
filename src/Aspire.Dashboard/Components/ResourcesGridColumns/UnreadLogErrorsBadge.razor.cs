// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Model;
using Aspire.Dashboard.Otlp.Storage;
using Aspire.Dashboard.Utils;
using Microsoft.AspNetCore.Components;

namespace Aspire.Dashboard.Components;

public partial class UnreadLogErrorsBadge
{
    private string? _resourceName;
    private int _unviewedCount;

    [Parameter, EditorRequired]
    public required ResourceViewModel Resource { get; set; }

    [Parameter, EditorRequired]
    public required Dictionary<ResourceKey, int>? UnviewedErrorCounts { get; set; }

    [Inject]
    private DashboardDataSource DataSource { get; set; } = null!;

    public ITelemetryRepository TelemetryRepository => DataSource.TelemetryRepository;

    protected override void OnParametersSet()
    {
        (_resourceName, _unviewedCount) = GetUnviewedErrorCount(Resource);
    }

    private (string? resourceName, int unviewedErrorCount) GetUnviewedErrorCount(ResourceViewModel resource)
    {
        if (UnviewedErrorCounts is null)
        {
            return (null, 0);
        }

        var otlpResource = TelemetryRepository.GetResourceByCompositeName(resource.Name);
        if (otlpResource is null)
        {
            return (null, 0);
        }

        if (!UnviewedErrorCounts.TryGetValue(otlpResource.ResourceKey, out var count) || count == 0)
        {
            return (null, 0);
        }

        var resources = TelemetryRepository.GetResources();
        var resourceName = resources.Count(a => a.ResourceName == otlpResource.ResourceName) > 1
            ? otlpResource.InstanceId
            : otlpResource.ResourceName;

        return (resourceName, count);
    }

    private string GetResourceErrorStructuredLogsUrl()
    {
        return DashboardUrls.StructuredLogsUrl(resource: _resourceName, logLevel: "error");
    }
}
