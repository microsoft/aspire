// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Serialization;
using k8s.Models;

namespace Aspire.Hosting.Dcp.Model.V2;

internal sealed class ContainerExecConfig
{
    [JsonPropertyName("command")]
    public string Command { get; set; } = "";

    [JsonPropertyName("args")]
    public List<string>? Args { get; set; }

    [JsonPropertyName("workingDirectory")]
    public string? WorkingDirectory { get; set; }

    [JsonPropertyName("env")]
    public List<EnvVar>? Env { get; set; }

    [JsonPropertyName("envFiles")]
    public List<string>? EnvFiles { get; set; }
}

internal sealed class LogicalContainerExecSpec
{
    [JsonPropertyName("containerRef")]
    public string ContainerRef { get; set; } = "";

    [JsonPropertyName("execution")]
    public ContainerExecConfig Execution { get; set; } = new();
}

internal sealed class LogicalContainerExecStatus
{
    [JsonPropertyName("phase")]
    public string? Phase { get; set; }

    [JsonPropertyName("startedAt")]
    [JsonConverter(typeof(KubernetesMicroTimeJsonConverter))]
    public DateTime? StartedAt { get; set; }

    [JsonPropertyName("finishedAt")]
    [JsonConverter(typeof(KubernetesMicroTimeJsonConverter))]
    public DateTime? FinishedAt { get; set; }

    [JsonPropertyName("exitCode")]
    public int? ExitCode { get; set; }

    [JsonPropertyName("conditions")]
    public List<V1Condition>? Conditions { get; set; }

    [JsonPropertyName("physicalExecRef")]
    public string? PhysicalExecRef { get; set; }

    [JsonPropertyName("containerID")]
    public string? ContainerId { get; set; }

    [JsonPropertyName("runtimeLogs")]
    public LogSource? RuntimeLogs { get; set; }
}

internal static class ContainerExecPhase
{
    public const string Pending = "Pending";
    public const string Running = "Running";
    public const string Exited = "Exited";
    public const string Failed = "Failed";
}

internal sealed class LogicalContainerExec(LogicalContainerExecSpec spec) : CustomResource<LogicalContainerExecSpec, LogicalContainerExecStatus>(spec), IKubernetesStaticMetadata
{
    public static string ObjectKind => "LogicalContainerExec";

    public static LogicalContainerExec Create(string name, string namespaceName, string containerRef)
    {
        return new(new() { ContainerRef = containerRef })
        {
            Kind = ObjectKind,
            ApiVersion = Dcp.GroupVersion.ToString(),
            Metadata = new() { Name = name, NamespaceProperty = namespaceName }
        };
    }
}
