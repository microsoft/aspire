// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREPROJECTS001
#pragma warning disable ASPIREEXTENSION001

using System.Diagnostics;
using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.Configuration;

namespace Aspire.Hosting.Dcp;

internal sealed class ExecutableLaunchPolicy(IConfiguration configuration)
{
    private readonly IConfiguration _configuration = configuration;

    public ExecutableLaunchDecision Decide(IResource resource)
    {
        if (resource.HasAnnotationOfType<ForceProcessExecutionAnnotation>() || resource.HasPersistentLifetime())
        {
            return new(
                ExecutableLaunchMechanism.Process,
                ExecutableLaunchMode.NoDebug,
                projectLaunchMode: GetProjectLaunchMode());
        }

        var supportsDebugging = resource.SupportsDebugging(_configuration, out var debugSupport);

        if (resource is ProjectResource)
        {
            if (resource.TryGetLastAnnotation<ProjectLaunchArgsOverrideAnnotation>(out _))
            {
                // A project launch override is a complete Process invocation. A supported custom debug producer
                // may still contribute optional launch metadata, matching the existing MAUI behavior, but its
                // success does not determine whether the Process invocation can run.
                var metadataProducer = supportsDebugging &&
                    debugSupport is { LaunchConfigurationType: not KnownLaunchConfigurationTypes.Project }
                        ? debugSupport
                        : null;
                return new(
                    ExecutableLaunchMechanism.Process,
                    GetLaunchMode(metadataProducer?.LaunchConfigurationType),
                    metadataProducer,
                    projectLaunchMode: GetProjectLaunchMode());
            }

            if (supportsDebugging && debugSupport is not null)
            {
                return new(
                    ExecutableLaunchMechanism.Ide,
                    GetLaunchMode(debugSupport.LaunchConfigurationType),
                    debugSupport,
                    projectLaunchMode: GetProjectLaunchMode());
            }

            if (ShouldUseCompatibilityProjectLaunch(resource, debugSupport))
            {
                return new(
                    ExecutableLaunchMechanism.Ide,
                    GetLaunchMode(KnownLaunchConfigurationTypes.Project),
                    useCompatibilityProjectLaunchConfiguration: true,
                    projectLaunchMode: GetProjectLaunchMode());
            }
        }
        else if (supportsDebugging && debugSupport is not null)
        {
            return new(
                ExecutableLaunchMechanism.Ide,
                GetLaunchMode(debugSupport.LaunchConfigurationType),
                debugSupport);
        }

        return new(
            ExecutableLaunchMechanism.Process,
            ExecutableLaunchMode.NoDebug,
            projectLaunchMode: GetProjectLaunchMode());
    }

    private bool ShouldUseCompatibilityProjectLaunch(
        IResource resource,
        SupportsDebuggingAnnotation? debugSupport)
    {
        if (string.IsNullOrEmpty(_configuration[DcpExecutor.DebugSessionPortVar]))
        {
            return false;
        }

        if (resource.TryGetLastAnnotation<ExecutableAnnotation>(out _) &&
            debugSupport?.LaunchConfigurationType is not null and not KnownLaunchConfigurationTypes.Project)
        {
            return false;
        }

        if (debugSupport is not null && !string.IsNullOrEmpty(_configuration[KnownConfigNames.DebugSessionInfo]))
        {
            return false;
        }

        // A directly added legacy ProjectResource may not declare a launch configuration type at all. Preserve its
        // historical Visual Studio-compatible behavior and treat it as a project launch whenever a debug session is
        // active. Project v2 resources declare their capability explicitly and do not use this compatibility path.
        return true;
    }

    private string GetLaunchMode(string? launchConfigurationType)
    {
        if (launchConfigurationType is KnownLaunchConfigurationTypes.Project)
        {
            return GetProjectLaunchMode();
        }

        return _configuration[KnownConfigNames.DebugSessionRunMode] ?? ExecutableLaunchMode.NoDebug;
    }

    private string GetProjectLaunchMode() =>
        _configuration[KnownConfigNames.DebugSessionRunMode]
        ?? (Debugger.IsAttached ? ExecutableLaunchMode.Debug : ExecutableLaunchMode.NoDebug);
}
