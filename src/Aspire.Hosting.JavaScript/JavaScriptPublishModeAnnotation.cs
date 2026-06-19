// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.JavaScript;

internal enum JavaScriptPublishMode
{
    StaticWebsite,
    NodeServer,
    PackageScript,
    NextStandalone
}

internal sealed class JavaScriptPublishModeAnnotation(JavaScriptPublishMode mode) : IResourceAnnotation
{
    public JavaScriptPublishMode Mode { get; } = mode;

    public string OutputPath { get; init; } = "dist";

    // NodeServer properties
    public string? EntryPoint { get; init; }

    // PackageScript properties
    public string? ScriptName { get; init; }
    public string? RunScriptArguments { get; init; }
}
