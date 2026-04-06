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

        // Per DCP IDE execution spec, project launch configuration support is implicit.
        // Custom launch types (for example, azure-functions) must be explicitly advertised.
        return supportsDebuggingAnnotation.LaunchConfigurationType == "project"
            || (supportedLaunchConfigurations is not null && supportedLaunchConfigurations.Contains(supportsDebuggingAnnotation.LaunchConfigurationType));
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
