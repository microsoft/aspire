// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Cpp;

internal sealed class CMakeAppArgsAnnotation(object[] args) : IResourceAnnotation
{
    public object[] Args { get; } = args;
}

internal sealed class CMakeConfigureArgsAnnotation(string[] args) : IResourceAnnotation
{
    public string[] Args { get; } = args;
}

internal sealed class CMakeBuildArgsAnnotation(string[] args) : IResourceAnnotation
{
    public string[] Args { get; } = args;
}

internal sealed class CMakeBuildTypeAnnotation(string buildType) : IResourceAnnotation
{
    public string BuildType { get; } = buildType;
}

internal sealed class CMakeVcpkgAnnotation(string? root) : IResourceAnnotation
{
    public string? Root { get; } = root;
}

internal sealed class CMakeInstallAnnotation(string? executableRelativePath) : IResourceAnnotation
{
    public string? ExecutableRelativePath { get; } = executableRelativePath;
}

internal sealed class CMakeConfigureResourceAnnotation(IResourceBuilder<ExecutableResource> resourceBuilder) : IResourceAnnotation
{
    public IResourceBuilder<ExecutableResource> ResourceBuilder { get; } = resourceBuilder;
}

internal sealed class CMakeBuildResourceAnnotation(IResourceBuilder<ExecutableResource> resourceBuilder) : IResourceAnnotation
{
    public IResourceBuilder<ExecutableResource> ResourceBuilder { get; } = resourceBuilder;
}

internal sealed class CMakeInstallResourceAnnotation(IResourceBuilder<ExecutableResource> resourceBuilder) : IResourceAnnotation
{
    public IResourceBuilder<ExecutableResource> ResourceBuilder { get; } = resourceBuilder;
}
