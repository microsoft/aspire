// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Dcp.Model;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.Dcp;

internal sealed class DcpExecutableRenderer(ILogger<DcpExecutableRenderer> logger)
{
    private readonly ILogger<DcpExecutableRenderer> _logger = logger;

    public void Render(
        RenderedModelResource<Executable> renderedResource,
        ExecutableLaunchPlan plan,
        ExecutablePemCertificates? pemCertificates)
    {
        var executable = renderedResource.DcpResource;
        var spec = executable.Spec;

        // Executable objects are reused on restart. Apply every launch field from the completed immutable plan so
        // a failed prior attempt cannot leak stale execution type, arguments, environment, or launch metadata.
        spec.ExecutablePath = plan.Command;
        spec.WorkingDirectory = plan.WorkingDirectory;
        spec.ExecutionType = plan.Mechanism switch
        {
            ExecutableLaunchMechanism.Process => ExecutionType.Process,
            ExecutableLaunchMechanism.Ide => ExecutionType.IDE,
            _ => throw new InvalidOperationException($"Unknown executable launch mechanism '{plan.Mechanism}'.")
        };
        spec.Args = plan.Arguments?.ToList();
        spec.Env = plan.EnvironmentVariables
            .Select(static variable => new EnvVar { Name = variable.Key, Value = variable.Value })
            .ToList();
        spec.PemCertificates = pemCertificates;

        executable.Metadata.Annotations?.Remove(Executable.LaunchConfigurationsAnnotation);
        if (plan.LaunchConfigurations.Count > 0)
        {
            executable.SetAnnotationAsObjectList(Executable.LaunchConfigurationsAnnotation, plan.LaunchConfigurations);
        }

        executable.SetAnnotationAsObjectList(
            CustomResource.ResourceAppArgsAnnotation,
            plan.DisplayArguments.Select(static argument => new AppLaunchArgumentAnnotation(
                argument.Value,
                argument.IsSensitive,
                argument.EffectiveArgumentIndex)));

        ApplyLifetime(renderedResource.ModelResource, spec);
        ApplyTerminal(renderedResource.ModelResource, executable);
    }

    private static void ApplyLifetime(IResource resource, ExecutableSpec spec)
    {
        spec.Persistent = null;
        spec.MonitorPid = null;
        spec.MonitorTimestamp = null;

        if (resource.GetLifetimeType() != Lifetime.Persistent)
        {
            return;
        }

        spec.Persistent = true;
        if (resource.TryGetParentProcessLifetime(out var parentProcessId, out var parentProcessTimestamp))
        {
            spec.MonitorPid = parentProcessId;
            spec.MonitorTimestamp = parentProcessTimestamp;
        }
    }

    private void ApplyTerminal(IResource resource, Executable executable)
    {
        executable.Spec.Terminal = null;
        if (!resource.TryGetAnnotationsOfType<TerminalAnnotation>(out var terminalAnnotations) ||
            terminalAnnotations.FirstOrDefault() is not { } terminalAnnotation)
        {
            return;
        }

        if (TryGetReplicaIndex(executable, out var replicaIndex) &&
            replicaIndex >= 0 &&
            replicaIndex < terminalAnnotation.TerminalHosts.Count)
        {
            executable.Spec.Terminal = new TerminalSpec
            {
                UdsPath = terminalAnnotation.TerminalHosts[replicaIndex].Layout.ProducerUdsPath,
                // The Aspire terminal host owns the listener at UdsPath; DCP must dial it.
                SocketMode = "connect",
                Cols = terminalAnnotation.Options.Columns,
                Rows = terminalAnnotation.Options.Rows
            };
            return;
        }

        _logger.LogWarning(
            "Could not determine a producer UDS path for replica of resource '{ResourceName}'; terminal will not be attached for this replica.",
            resource.Name);
    }

    private static bool TryGetReplicaIndex(Executable executable, out int replicaIndex)
    {
        replicaIndex = -1;
        return executable.Metadata.Annotations is { } annotations &&
            annotations.TryGetValue(CustomResource.ResourceReplicaIndex, out var value) &&
            int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out replicaIndex);
    }
}
