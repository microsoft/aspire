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
        var debugSupportDecision = resource.GetEffectiveDebugSupportDecision(_configuration);
        var debugSupport = debugSupportDecision.EffectiveAnnotation;
        var supportsDebugging = debugSupport is not null;

        if (resource is ProjectResource)
        {
            if (resource.TryGetLastAnnotation<ProjectLaunchArgsOverrideAnnotation>(out _))
            {
                // A project launch override is a complete Process invocation. A supported custom debug producer
                // may still contribute optional launch metadata, matching the existing MAUI behavior, but its
                // success does not determine whether the Process invocation can run.
                var metadataProducer = supportsDebugging &&
                    debugSupport is not null &&
                    !KnownLaunchConfigurationTypes.IsProject(debugSupport.LaunchConfigurationType)
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
                    materializeLaunchProfileArguments: debugSupportDecision.UsesAzureFunctionsProjectCompatibility,
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

    private string GetLaunchMode(string? launchConfigurationType)
    {
        if (KnownLaunchConfigurationTypes.IsProject(launchConfigurationType))
        {
            return GetProjectLaunchMode();
        }

        return _configuration[KnownConfigNames.DebugSessionRunMode] ?? ExecutableLaunchMode.NoDebug;
    }

    private string GetProjectLaunchMode() =>
        _configuration[KnownConfigNames.DebugSessionRunMode]
        ?? (Debugger.IsAttached ? ExecutableLaunchMode.Debug : ExecutableLaunchMode.NoDebug);
}
