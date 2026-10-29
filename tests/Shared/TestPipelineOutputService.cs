// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Pipelines;

namespace Aspire.Tests.Utils;

internal sealed class TestPipelineOutputService(string root) : IPipelineOutputService
{
    public string GetOutputDirectory() => Path.Combine(root, "provider-global");
    public string GetOutputDirectory(IResource resource) => Path.Combine(root, $"provider-{resource.Name}");
    public string GetTempDirectory() => Path.Combine(root, "provider-temp");
    public string GetTempDirectory(IResource resource) => Path.Combine(GetTempDirectory(), resource.Name);
}
