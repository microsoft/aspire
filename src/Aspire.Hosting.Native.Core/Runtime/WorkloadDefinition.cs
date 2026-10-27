// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Native.Runtime;

/// <summary>Declares a named endpoint independently of container or executable implementation.</summary>
internal sealed record WorkloadPort(string Name, WorkloadProtocol Protocol, string UriScheme, int TargetPort, int HostPort,
    string EnvironmentVariable)
{
    public bool IsExternal { get; init; }
}

/// <summary>Contains a named host-facing allocation, not a container-network address.</summary>
internal sealed record AllocatedWorkloadPort(string Name, WorkloadProtocol Protocol, string UriScheme, string Host, int Port);

/// <summary>Describes a bind mount or an explicitly named externally retained volume.</summary>
internal sealed record WorkloadMount(WorkloadMountKind Type, string Source, string Target, bool ReadOnly);

/// <summary>Validates launch declarations before granting an executor a cleanup obligation.</summary>
internal static class WorkloadDefinition
{
    public static void Validate(WorkloadPlan plan) => ValidateCore(plan, requireLocalPaths: true);

    /// <summary>Validates desired intent without requiring a deployment host's paths to exist on the authoring machine.</summary>
    public static void ValidateDeclaration(WorkloadPlan plan) => ValidateCore(plan, requireLocalPaths: false);

    private static void ValidateCore(WorkloadPlan plan, bool requireLocalPaths)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ValidateEnvironmentAndArguments(plan);
        ValidateEndpoints(plan);
        switch (plan)
        {
            case ContainerWorkload container:
                ValidateContainer(container, requireLocalPaths);
                break;
            case ExecutableWorkload executable:
                ValidateExecutable(executable, requireLocalPaths);
                break;
            default:
                throw new NotSupportedException("The workload primitive is not implemented.");
        }
    }

    private static void ValidateEnvironmentAndArguments(WorkloadPlan plan)
    {
        if (plan.Environment.IsDefault || plan.Arguments.IsDefault || plan.Endpoints.IsDefault ||
            plan.Environment.Length > 64 || plan.Arguments.Length > 128 || plan.Endpoints.Length > 32 ||
            plan.Arguments.Any(argument => argument is null || argument.Length > 8192 || argument.Contains('\0')) ||
            plan.Environment.Any(variable => variable is null || !ValidEnvironmentName(variable.Name) ||
                variable.Value is null || variable.Value.Length > 8192 || variable.Value.Contains('\0')) ||
            plan.Environment.Select(variable => variable.Name).Distinct(StringComparer.Ordinal).Count() != plan.Environment.Length)
        {
            throw new ArgumentException("Invalid workload environment, arguments, or endpoint capacity.", nameof(plan));
        }
    }

    private static void ValidateEndpoints(WorkloadPlan plan)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var environmentNames = plan.Environment.Select(variable => variable.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var endpoint in plan.Endpoints)
        {
            if (endpoint is null || string.IsNullOrEmpty(endpoint.Name) || endpoint.Name.Length > 64 ||
                !char.IsAsciiLetter(endpoint.Name[0]) ||
                endpoint.Name.Any(character => !(char.IsAsciiLetterOrDigit(character) || character == '-')) ||
                !names.Add(endpoint.Name) || endpoint.Protocol is not (WorkloadProtocol.Tcp or WorkloadProtocol.Udp) ||
                endpoint.UriScheme is null || endpoint.UriScheme.Length > 64 || !Uri.CheckSchemeName(endpoint.UriScheme) ||
                endpoint.TargetPort is < 0 or > 65535 || endpoint.HostPort is < 0 or > 65535 ||
                endpoint.EnvironmentVariable is null ||
                endpoint.EnvironmentVariable.Length > 0 &&
                    (!ValidEnvironmentName(endpoint.EnvironmentVariable) || !environmentNames.Add(endpoint.EnvironmentVariable)))
            {
                throw new ArgumentException("Invalid or duplicate named workload endpoint.", nameof(plan));
            }
            if (plan is ContainerWorkload && (endpoint.TargetPort == 0 || endpoint.EnvironmentVariable.Length > 0) ||
                plan is ExecutableWorkload && endpoint.TargetPort == 0 && endpoint.EnvironmentVariable.Length == 0 ||
                endpoint.TargetPort > 0 && endpoint.EnvironmentVariable.Length > 0)
            {
                throw new ArgumentException("Use a container target port or an executable target port/port environment variable.", nameof(plan));
            }
        }
    }

    private static void ValidateContainer(ContainerWorkload plan, bool requireLocalPaths)
    {
        if (string.IsNullOrWhiteSpace(plan.Image) || plan.Image.Length > 2048 ||
            plan.Image.Any(char.IsWhiteSpace) || plan.Image.Contains('\0') ||
            plan.Command is { } command && (string.IsNullOrWhiteSpace(command) || command.Length > 8192 || command.Contains('\0')) ||
            plan.Mounts.IsDefault || plan.Mounts.Length > 64)
        {
            throw new ArgumentException("Invalid container image, command, or mounts.", nameof(plan));
        }
        ValidateMounts(plan, requireLocalPaths);
        if (plan.Endpoints.Select(endpoint => (endpoint.TargetPort, endpoint.Protocol)).Distinct().Count() != plan.Endpoints.Length)
        {
            throw new ArgumentException("Container target ports must be unique for each protocol.", nameof(plan));
        }
    }

    private static void ValidateMounts(ContainerWorkload plan, bool requireLocalPaths)
    {
        var targets = new HashSet<string>(StringComparer.Ordinal);
        foreach (var mount in plan.Mounts)
        {
            if (mount is null || mount.Source is null || string.IsNullOrWhiteSpace(mount.Target) ||
                !mount.Target.StartsWith('/') || mount.Target.Length > 4096 || mount.Target.Contains('\0') ||
                !targets.Add(mount.Target) ||
                mount.Type is not (WorkloadMountKind.Bind or WorkloadMountKind.Volume) ||
                mount.Type == WorkloadMountKind.Bind && (!Path.IsPathFullyQualified(mount.Source) ||
                    requireLocalPaths && !(File.Exists(mount.Source) || Directory.Exists(mount.Source))) ||
                mount.Type == WorkloadMountKind.Volume && (mount.Source.Length is < 1 or > 255 ||
                    !char.IsAsciiLetterOrDigit(mount.Source[0]) ||
                    mount.Source.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '_' or '.' or '-'))))
            {
                throw new ArgumentException("Invalid or duplicate container mount.", nameof(plan));
            }
        }
    }

    private static void ValidateExecutable(ExecutableWorkload plan, bool requireLocalPaths)
    {
        if (!Path.IsPathFullyQualified(plan.ExecutablePath) || requireLocalPaths && !File.Exists(plan.ExecutablePath) ||
            !Path.IsPathFullyQualified(plan.WorkingDirectory) || requireLocalPaths && !Directory.Exists(plan.WorkingDirectory))
        {
            throw new ArgumentException("Executables require existing absolute paths.", nameof(plan));
        }
    }

    private static bool ValidEnvironmentName(string name) =>
        !string.IsNullOrEmpty(name) && name.Length <= 128 &&
        !name.Contains('=') && !name.Contains('\0');
}
