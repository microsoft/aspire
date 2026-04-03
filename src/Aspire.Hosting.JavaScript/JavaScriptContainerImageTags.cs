// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.JavaScript;

internal static class JavaScriptContainerImageTags
{
    /// <summary>
    /// YARP reverse proxy image used by <see cref="JavaScriptHostingExtensions.PublishAsStaticWebsite{TResource}(ApplicationModel.IResourceBuilder{TResource}, System.Action{PublishAsStaticWebsiteOptions}?)"/>.
    /// </summary>
    public const string YarpRegistry = "mcr.microsoft.com";

    public const string YarpImage = "dotnet/nightly/yarp";

    public const string YarpTag = "2.3-preview";
}
