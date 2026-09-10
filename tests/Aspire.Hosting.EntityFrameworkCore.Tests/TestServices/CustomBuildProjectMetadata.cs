// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.EntityFrameworkCore.Tests.TestServices;

internal sealed class CustomBuildProjectMetadata : IProjectMetadata
{
    public CustomBuildProjectMetadata()
    {
    }

    public string ProjectPath { get; init; } = new Projects.ServiceB().ProjectPath;

    public IReadOnlyDictionary<string, string> BuildEnvironment { get; } =
        new Dictionary<string, string> { ["BUILD_FLAVOR"] = "do-not-log-this-build-value" };
}
