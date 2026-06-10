// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Dcp;
using Aspire.Hosting.Dcp.Model;
using Microsoft.Extensions.Configuration;

namespace Aspire.Hosting.Utils;

#pragma warning disable ASPIREEXTENSION001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
internal static class ExtensionUtils
{
    [AspireExportIgnore(Reason = "Debug support inspection is a local .NET helper and is not part of the ATS surface.")]
    public static bool SupportsDebugging(this IResource builder, IConfiguration configuration, [NotNullWhen(true)] out SupportsDebuggingAnnotation? supportsDebuggingAnnotation)
    {
        var supportedLaunchConfigurations = GetSupportedLaunchConfigurations(configuration);

        if (!builder.TryGetLastAnnotation(out supportsDebuggingAnnotation)
            || string.IsNullOrEmpty(configuration[DcpExecutor.DebugSessionPortVar]))
        {
            return false;
        }

        // When the IDE did not send DEBUG_SESSION_INFO (e.g. Visual Studio), fall back to the
        // legacy rule that "project" launch configuration support is implicit. VS launches all
        // project resources natively without advertising a capability list.
        if (supportedLaunchConfigurations is null)
        {
            return supportsDebuggingAnnotation.LaunchConfigurationType == "project";
        }

        // The IDE advertised an explicit capability list — honor it for every type, including
        // "project". An IDE that can launch project resources must include "project" in its list
        // (the VS Code extension does this when the C# extension is installed). Treating "project"
        // as implicitly supported here would route resources to an IDE that cannot launch them
        // and leave them stuck.
        return supportedLaunchConfigurations.Contains(supportsDebuggingAnnotation.LaunchConfigurationType);
    }

    private static string[]? GetSupportedLaunchConfigurations(IConfiguration configuration)
    {
        try
        {
            if (configuration[KnownConfigNames.DebugSessionInfo] is { } debugSessionInfoJson && JsonSerializer.Deserialize<RunSessionInfo>(debugSessionInfoJson) is { } debugSessionInfo)
            {
                return debugSessionInfo.SupportedLaunchConfigurations;
            }
        }
        catch (JsonException)
        {
        }

        return null;
    }
}
