// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting;

// Stores browser automation configuration chosen from the dashboard for the current AppHost process. User secrets persist the
// same values for the next run, but the store makes the next command execution use the new values immediately without
// depending on configuration reload timing.
internal sealed class BrowserLogsConfigurationStore
{
    private readonly object _lock = new();
    private readonly Dictionary<string, BrowserConfiguration> _resourceConfigurations = new(StringComparers.ResourceName);
    private BrowserConfiguration? _globalConfiguration;

    public (BrowserConfiguration? Resource, BrowserConfiguration? Global) GetConfigurations(string resourceName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceName);

        lock (_lock)
        {
            var hasResourceConfiguration = _resourceConfigurations.TryGetValue(resourceName, out var resourceConfiguration);
            return (hasResourceConfiguration ? resourceConfiguration : null, _globalConfiguration);
        }
    }

    public void Set(BrowserLogsConfigurationScope scope, string resourceName, BrowserConfiguration configuration)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceName);

        lock (_lock)
        {
            if (scope == BrowserLogsConfigurationScope.Global)
            {
                _globalConfiguration = configuration;
            }
            else
            {
                _resourceConfigurations[resourceName] = configuration;
            }
        }
    }
}

internal enum BrowserLogsConfigurationScope
{
    Resource,
    Global
}
