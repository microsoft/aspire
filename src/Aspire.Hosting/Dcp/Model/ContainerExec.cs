// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Serialization;
using k8s.Models;

namespace Aspire.Hosting.Dcp.Model;

internal sealed class ContainerExecSpec
{
    /// <summary>
    /// The name of the <see cref="Container"/> resource (DCP model name, not the Docker/Podman name)
    /// to execute the command in. If no resource exists with the given name, the command will remain in
    /// a pending state until a resource with the given name is created.
    /// </summary>
    [JsonPropertyName("containerName")]
    public string? ContainerName { get; set; }

    /// <summary>
    /// Optional working directory for the command (in the container filesystem).
    /// </summary>
    [JsonPropertyName("workingDirectory")]
    public string? WorkingDirectory { get; set; }

    /// <summary>
    /// Optional environment variables to set for the executing command.
    /// </summary>
    [JsonPropertyName("env")]
    public List<EnvVar>? Env { get; set; }

    /// <summary>
    /// Optional environment files to use to populate Container environment during startup
    /// </summary>
    [JsonPropertyName("envFiles")]
    public List<string>? EnvFiles { get; set; }

    /// <summary>
    /// Command to run in the container.
    /// </summary>
    [JsonPropertyName("command")]
    public string? Command { get; set; }

    /// <summary>
    /// Optional arguments to pass to the command that starts the container.
    /// </summary>
    [JsonPropertyName("args")]
    public List<string>? Args { get; set; }
}

internal sealed record ContainerExecStatus : V1Status
{
    /// <summary>
    /// The current state of the container execution. See <see cref="ExecutableState"/> for possible values.
    /// </summary>
    [JsonPropertyName("state")]
    public string? State { get; set; } = ExecutableState.Unknown;

    /// <summary>
    /// Start (attempt) timestamp.
    /// </summary>
    [JsonPropertyName("startupTimestamp")]
    public DateTime? StartupTimestamp { get; set; }

    /// <summary>
    /// The time when the command finished execution
    /// </summary>
    [JsonPropertyName("finishTimestamp")]
    public DateTime? FinishTimestamp { get; set; }

    /// <summary>
    /// Exit code of the process associated with the ContainerExec.
    /// The value is equal to UnknownExitCode if the command was not started, is still running, or the exit code is not available.
    /// </summary>
    [JsonPropertyName("exitCode")]
    public int? ExitCode { get; set; }

    /// <summary>
    /// The path of a temporary file that contains captured standard output data from the ContainerExec command.
    /// </summary>
    [JsonPropertyName("stdOutFile")]
    public string? StdOutFile { get; set; }

    /// <summary>
    /// The path of a temporary file that contains captured standard error data from the ContainerExec command.
    /// </summary>
    [JsonPropertyName("stdErrFile")]
    public string? StdErrFile { get; set; }

    /// <summary>
    /// Effective values of environment variables, after all substitutions have been applied
    /// </summary>
    [JsonPropertyName("effectiveEnv")]
    public List<EnvVar>? EffectiveEnv { get; set; }

    /// <summary>
    /// Effective values of launch arguments to be passed to the ContainerExec, after all substitutions are applied.
    /// </summary>
    [JsonPropertyName("effectiveArgs")]
    public List<string>? EffectiveArgs { get; set; }
}

/// <summary>
/// Represents a command to be executed in a given <see cref="Container"/> resource.
/// </summary>
internal sealed class ContainerExec : CustomResource<ContainerExecSpec, ContainerExecStatus>, IKubernetesStaticMetadata
{
    /// <summary>
    /// Create a new <see cref="ContainerExec"/> resource.
    /// </summary>
    /// <param name="spec">The <see cref="ContainerExecSpec"/> describing the new resource</param>
    [JsonConstructor]
    public ContainerExec(ContainerExecSpec spec) : base(spec) { }

    /// <summary>
    /// Create a new <see cref="ContainerExec"/> resource.
    /// </summary>
    /// <param name="name">Resource name of the ContainerExec instance</param>
    /// <param name="containerName">Resource name of the Container to run the command in</param>
    /// <param name="command">The command name to run</param>
    /// <param name="args">Arguments of the command to run</param>
    /// <param name="workingDirectory">Container working directory to run the command in</param>
    /// <returns>A new ContainerExec instance</returns>
    public static ContainerExec Create(string name, string containerName, string command, List<string>? args = null, string? workingDirectory = null)
    {
        var containerExec = new ContainerExec(new ContainerExecSpec
        {
            ContainerName = containerName,
            Command = command,
            Args = args,
            WorkingDirectory = workingDirectory
        })
        {
            Kind = Dcp.ContainerExecKind,
            ApiVersion = Dcp.GroupVersion.ToString()
        };

        containerExec.Metadata.Name = name;
        containerExec.Metadata.NamespaceProperty = string.Empty;

        return containerExec;
    }

    public bool LogsAvailable => !string.IsNullOrEmpty(this.Status?.State);

    public static string ObjectKind => Dcp.ContainerExecKind;
}
