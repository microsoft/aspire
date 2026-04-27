// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.Configuration;

namespace Aspire.Hosting;

internal sealed class BrowserLogsResource(
    string name,
    IResourceWithEndpoints parentResource,
    BrowserConfiguration initialConfiguration,
    BrowserConfigurationOverrides configurationOverrides)
    : Resource(name)
{
    public IResourceWithEndpoints ParentResource { get; } = parentResource;

    public BrowserConfiguration InitialConfiguration { get; } = initialConfiguration;

    public BrowserConfigurationOverrides ConfigurationOverrides { get; } = configurationOverrides;

    public BrowserConfiguration ResolveCurrentConfiguration(IConfiguration configuration) =>
        BrowserConfiguration.Resolve(configuration, ParentResource.Name, ConfigurationOverrides);
}
