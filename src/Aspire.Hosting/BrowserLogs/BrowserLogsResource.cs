// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.Configuration;

namespace Aspire.Hosting;

internal sealed class BrowserLogsResource(
    string name,
    IResourceWithEndpoints parentResource,
    BrowserConfiguration initialConfiguration,
    string? browserOverride,
    string? profileOverride,
    BrowserUserDataMode? userDataModeOverride)
    : Resource(name)
{
    public IResourceWithEndpoints ParentResource { get; } = parentResource;

    public string Browser { get; } = initialConfiguration.Browser;

    public string? Profile { get; } = initialConfiguration.Profile;

    public BrowserUserDataMode UserDataMode { get; } = initialConfiguration.UserDataMode;

    public string? BrowserOverride { get; } = browserOverride;

    public string? ProfileOverride { get; } = profileOverride;

    public BrowserUserDataMode? UserDataModeOverride { get; } = userDataModeOverride;

    public BrowserConfiguration ResolveCurrentConfiguration(IConfiguration configuration) =>
        BrowserConfiguration.Resolve(configuration, ParentResource.Name, BrowserOverride, ProfileOverride, UserDataModeOverride);
}
