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
        var effectiveDebugSupport = debugSupportDecision.EffectiveAnnotation;

        if (resource is ProjectResource)
        {
            if (resource.TryGetLastAnnotation<ProjectLaunchArgsOverrideAnnotation>(out _))
            {
                // A project launch override is a complete Process invocation. A supported custom debug producer
                // may still contribute optional launch metadata, matching the existing MAUI behavior, but its
                // success does not determine whether the Process invocation can run.
                var metadataProducer =
                    debugSupportDecision.SupportedDeclaredAnnotation is { } supportedDeclaredAnnotation &&
                    !KnownLaunchConfigurationTypes.IsProject(supportedDeclaredAnnotation.LaunchConfigurationType)
                        ? supportedDeclaredAnnotation
                        : null;
                return new(
                    ExecutableLaunchMechanism.Process,
                    GetLaunchMode(metadataProducer?.LaunchConfigurationType),
                    metadataProducer,
                    projectLaunchMode: GetProjectLaunchMode());
            }

            if (effectiveDebugSupport is not null)
            {
                if (debugSupportDecision.UsesCompatibilityProjectLaunch)
                {
                    return new(
                        ExecutableLaunchMechanism.Ide,
                        GetLaunchMode(effectiveDebugSupport.LaunchConfigurationType),
                        useCompatibilityProjectLaunchConfiguration: true,
                        materializeLaunchProfileArguments: debugSupportDecision.UsesAzureFunctionsProjectCompatibility,
                        projectLaunchMode: GetProjectLaunchMode());
                }

                var declaredProducer = debugSupportDecision.SupportedDeclaredAnnotation ??
                    throw new InvalidOperationException(
                        $"Resource '{resource.Name}' selected declared debug support without an active declared producer.");

                return new(
                    ExecutableLaunchMechanism.Ide,
                    GetLaunchMode(declaredProducer.LaunchConfigurationType),
                    declaredProducer,
                    projectLaunchMode: GetProjectLaunchMode());
            }
        }
        else if (effectiveDebugSupport is not null)
        {
            var declaredProducer = debugSupportDecision.SupportedDeclaredAnnotation ??
                throw new InvalidOperationException(
                    $"Resource '{resource.Name}' selected declared debug support without an active declared producer.");

            return new(
                ExecutableLaunchMechanism.Ide,
                GetLaunchMode(declaredProducer.LaunchConfigurationType),
                declaredProducer);
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
