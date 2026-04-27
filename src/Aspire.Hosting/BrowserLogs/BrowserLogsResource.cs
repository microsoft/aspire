// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.Configuration;

namespace Aspire.Hosting;

internal readonly record struct BrowserLogsSettings(string Browser, string? Profile);

internal sealed class BrowserLogsResource(
    string name,
    IResourceWithEndpoints parentResource,
    BrowserLogsSettings initialSettings,
    string? browserOverride,
    string? profileOverride)
    : Resource(name)
{
    public IResourceWithEndpoints ParentResource { get; } = parentResource;

    public string Browser { get; } = initialSettings.Browser;

    public string? Profile { get; } = initialSettings.Profile;

    public string? BrowserOverride { get; } = browserOverride;

    public string? ProfileOverride { get; } = profileOverride;

    public BrowserLogsSettings ResolveCurrentSettings(IConfiguration configuration) =>
        BrowserLogsBuilderExtensions.ResolveSettings(configuration, ParentResource.Name, BrowserOverride, ProfileOverride);
}
