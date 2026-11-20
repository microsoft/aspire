// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Serialization;
using k8s.Models;

namespace Aspire.Hosting.Dcp.Model.V2;

internal sealed class LogicalProcessSpec
{
    [JsonPropertyName("mode")]
    public string? Mode { get; set; }

    [JsonPropertyName("lifecycleKey")]
    public string? LifecycleKey { get; set; }

    [JsonPropertyName("process")]
    public LogicalProcessConfig? Process { get; set; }

    [JsonPropertyName("ports")]
    public List<LogicalProcessPort>? Ports { get; set; }

    [JsonPropertyName("pid")]
    public long? Pid { get; set; }

    [JsonPropertyName("identityTimestamp")]
    [JsonConverter(typeof(KubernetesMicroTimeJsonConverter))]
    public DateTime? IdentityTimestamp { get; set; }

    [JsonPropertyName("monitorPID")]
    public long? MonitorPid { get; set; }

    [JsonPropertyName("monitorTimestamp")]
    [JsonConverter(typeof(KubernetesMicroTimeJsonConverter))]
    public DateTime? MonitorTimestamp { get; set; }

    [JsonPropertyName("stop")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Stop { get; set; }
}

internal sealed class LogicalProcessConfig
{
    [JsonPropertyName("executablePath")]
    public string ExecutablePath { get; set; } = "";

    [JsonPropertyName("args")]
    public List<string>? Args { get; set; }

    [JsonPropertyName("workingDirectory")]
    public string? WorkingDirectory { get; set; }

    [JsonPropertyName("ambientEnvironment")]
    public AmbientEnvironment? AmbientEnvironment { get; set; }

    [JsonPropertyName("env")]
    public List<EnvVar>? Env { get; set; }
}

internal sealed class LogicalProcessStatus
{
    [JsonPropertyName("ports")]
    public List<LogicalPortStatus>? Ports { get; set; }

    [JsonPropertyName("runtimeLogs")]
    public LogSource? RuntimeLogs { get; set; }

    [JsonPropertyName("phase")]
    public string? Phase { get; set; }

    [JsonPropertyName("physicalProcessRef")]
    public string? PhysicalProcessRef { get; set; }

    [JsonPropertyName("lifecycleKey")]
    public string? LifecycleKey { get; set; }

    [JsonPropertyName("pid")]
    public long? Pid { get; set; }

    [JsonPropertyName("identityTimestamp")]
    [JsonConverter(typeof(KubernetesMicroTimeJsonConverter))]
    public DateTime? IdentityTimestamp { get; set; }

    [JsonPropertyName("finishedAt")]
    [JsonConverter(typeof(KubernetesMicroTimeJsonConverter))]
    public DateTime? FinishedAt { get; set; }

    [JsonPropertyName("exitCode")]
    public int? ExitCode { get; set; }

    [JsonPropertyName("conditions")]
    public List<V1Condition>? Conditions { get; set; }
}

internal static class LogicalProcessMode
{
    public const string Session = "session";
    public const string Persistent = "persistent";
    public const string Existing = "existing";
}

internal static class LogicalProcessPhase
{
    public const string Pending = "Pending";
    public const string Running = "Running";
    public const string Exited = "Exited";
    public const string Unknown = "Unknown";
    public const string Failed = "Failed";
}

internal sealed class LogicalProcess(LogicalProcessSpec spec) : CustomResource<LogicalProcessSpec, LogicalProcessStatus>(spec), IKubernetesStaticMetadata
{
    public static string ObjectKind => "LogicalProcess";

    public static LogicalProcess Create(string name, string namespaceName)
    {
        return new(new())
        {
            Kind = ObjectKind,
            ApiVersion = Dcp.GroupVersion.ToString(),
            Metadata = new() { Name = name, NamespaceProperty = namespaceName }
        };
    }
}
