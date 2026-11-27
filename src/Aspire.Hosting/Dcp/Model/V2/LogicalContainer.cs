// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Serialization;
using k8s.Models;

namespace Aspire.Hosting.Dcp.Model.V2;

internal sealed class LogicalContainerSpec
{
    [JsonPropertyName("containerName")]
    public string? ContainerName { get; set; }

    // Omission means session; persistent and existing require a runtime container name.
    [JsonPropertyName("mode")]
    public string? Mode { get; set; }

    [JsonPropertyName("lifecycleKey")]
    public string? LifecycleKey { get; set; }

    [JsonPropertyName("container")]
    public LogicalContainerConfig? Container { get; set; }

    [JsonPropertyName("ports")]
    public List<LogicalContainerPort>? Ports { get; set; }

    [JsonPropertyName("monitorPID")]
    public long? MonitorPid { get; set; }

    [JsonPropertyName("monitorTimestamp")]
    [JsonConverter(typeof(KubernetesMicroTimeJsonConverter))]
    public DateTime? MonitorTimestamp { get; set; }

    [JsonPropertyName("stop")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Stop { get; set; }
}

internal sealed class LogicalContainerConfig
{
    [JsonPropertyName("imageRef")]
    public string? ImageRef { get; set; }

    [JsonPropertyName("image")]
    public PhysicalContainerImageConfig? Image { get; set; }

    [JsonPropertyName("entrypoint")]
    public string? Entrypoint { get; set; }

    [JsonPropertyName("command")]
    public List<string>? Command { get; set; }

    [JsonPropertyName("env")]
    public List<EnvVar>? Env { get; set; }

    [JsonPropertyName("volumeMounts")]
    public List<VolumeMount>? VolumeMounts { get; set; }

    [JsonPropertyName("networks")]
    public List<LogicalContainerNetworkConnectionConfig>? Networks { get; set; }

    [JsonPropertyName("createFiles")]
    public List<CreateFileSystem>? CreateFiles { get; set; }

    [JsonPropertyName("labels")]
    public List<Label>? Labels { get; set; }
}

internal sealed class LogicalContainerNetworkConnectionConfig
{
    [JsonPropertyName("networkRef")]
    public string NetworkRef { get; set; } = "";

    [JsonPropertyName("aliases")]
    public List<string>? Aliases { get; set; }
}

internal sealed class LogicalContainerStatus
{
    [JsonPropertyName("ports")]
    public List<LogicalPortStatus>? Ports { get; set; }

    [JsonPropertyName("startupLogs")]
    public LogSource? StartupLogs { get; set; }

    [JsonPropertyName("runtimeLogs")]
    public LogSource? RuntimeLogs { get; set; }

    [JsonPropertyName("phase")]
    public string? Phase { get; set; }

    [JsonPropertyName("physicalContainerRef")]
    public string? PhysicalContainerRef { get; set; }

    [JsonPropertyName("imageRef")]
    public string? ImageRef { get; set; }

    [JsonPropertyName("containerID")]
    public string? ContainerId { get; set; }

    [JsonPropertyName("containerName")]
    public string? ContainerName { get; set; }

    [JsonPropertyName("image")]
    public string? Image { get; set; }

    [JsonPropertyName("lifecycleKey")]
    public string? LifecycleKey { get; set; }

    [JsonPropertyName("createdAt")]
    [JsonConverter(typeof(KubernetesMicroTimeJsonConverter))]
    public DateTime? CreatedAt { get; set; }

    [JsonPropertyName("startedAt")]
    [JsonConverter(typeof(KubernetesMicroTimeJsonConverter))]
    public DateTime? StartedAt { get; set; }

    [JsonPropertyName("finishedAt")]
    [JsonConverter(typeof(KubernetesMicroTimeJsonConverter))]
    public DateTime? FinishedAt { get; set; }

    [JsonPropertyName("exitCode")]
    public int? ExitCode { get; set; }

    [JsonPropertyName("portMappings")]
    public List<PhysicalContainerPortMapping>? PortMappings { get; set; }

    [JsonPropertyName("conditions")]
    public List<V1Condition>? Conditions { get; set; }
}

internal static class LogicalContainerMode
{
    public const string Session = "session";
    public const string Persistent = "persistent";
    public const string Existing = "existing";
}

internal static class LogicalContainerPhase
{
    public const string Pending = "Pending";
    public const string Running = "Running";
    public const string Paused = "Paused";
    public const string Stopping = "Stopping";
    public const string Exited = "Exited";
    public const string Unknown = "Unknown";
    public const string Failed = "Failed";
}

internal sealed class LogicalContainer(LogicalContainerSpec spec) : CustomResource<LogicalContainerSpec, LogicalContainerStatus>(spec), IKubernetesStaticMetadata
{
    public static string ObjectKind => "LogicalContainer";

    public static LogicalContainer Create(string name, string namespaceName)
    {
        return new(new())
        {
            Kind = ObjectKind,
            ApiVersion = Dcp.GroupVersion.ToString(),
            Metadata = new() { Name = name, NamespaceProperty = namespaceName }
        };
    }
}
