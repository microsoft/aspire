// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.Configuration;

namespace Aspire.Hosting;

internal sealed class BrowserAutomationResource(
    string name,
    IResourceWithEndpoints parentResource,
    BrowserConfiguration initialConfiguration,
    BrowserConfigurationExplicitValues explicitConfigurationValues)
    : Resource(name)
{
    public IResourceWithEndpoints ParentResource { get; } = parentResource;

    public BrowserConfiguration InitialConfiguration { get; } = initialConfiguration;

    public BrowserConfigurationExplicitValues ExplicitConfigurationValues { get; } = explicitConfigurationValues;

    public BrowserConfiguration ResolveCurrentConfiguration(IConfiguration configuration, BrowserLogsConfigurationStore? configurationStore = null)
    {
        var (resourceConfiguration, globalConfiguration) = configurationStore?.GetConfigurations(ParentResource.Name) ?? default;
        return BrowserConfiguration.Resolve(
            configuration,
            ParentResource.Name,
            ExplicitConfigurationValues,
            resourceConfiguration,
            globalConfiguration);
    }
}
