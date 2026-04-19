// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting;

internal sealed class BrowserLogsResource(string name, IResourceWithEndpoints parentResource, string browser)
    : Resource(name)
{
    public IResourceWithEndpoints ParentResource { get; } = parentResource;

    public string Browser { get; } = browser;
}
