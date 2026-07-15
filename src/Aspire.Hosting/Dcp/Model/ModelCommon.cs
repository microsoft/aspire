// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Dcp.Model;

using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using k8s;
using k8s.Models;

internal interface IAnnotationHolder
{
    void Annotate(string annotationName, string value);
    void AnnotateAsObjectList<TValue>(string annotationName, TValue value);
}

internal interface IKubernetesStaticMetadata
{
    static abstract string ObjectKind { get; }
}

internal abstract class CustomResource : KubernetesObject, IMetadata<V1ObjectMeta>, IAnnotationHolder
{
    public const string ServiceProducerAnnotation = "service-producer";
    public const string ServiceConsumerAnnotation = "service-consumer";
    public const string EndpointNameAnnotation = "endpoint-name";
    public const string ResourceNameAnnotation = "resource-name";
    public const string ContainerTunnelInstanceName = "container-tunnel-instance-name";
    public const string ContainerNetworkAnnotation = "container-network";
    public const string PrimaryServiceNameAnnotation = "primary-service-name";
    public const string OtelServiceNameAnnotation = "otel-service-name";
    public const string OtelServiceInstanceIdAnnotation = "otel-service-instance-id";
    public const string ResourceStateAnnotation = "resource-state";
    public const string ResourceAppArgsAnnotation = "resource-app-args";
    public const string ResourceProjectArgsAnnotation = "resource-project-args";
    public const string ResourceReplicaCount = "resource-replica-count";
    public const string ResourceReplicaIndex = "resource-replica-index";

    public string? AppModelResourceName => Metadata.Annotations?.TryGetValue(ResourceNameAnnotation, out var value) is true ? value : null;

    public string? AppModelInitialState => Metadata.Annotations?.TryGetValue(ResourceStateAnnotation, out var value) is true ? value : null;

    [JsonPropertyName("metadata")]
    public V1ObjectMeta Metadata { get; set; } = new V1ObjectMeta();

    public void Annotate(string annotationName, string value)
    {
        if (Metadata.Annotations is null)
        {
            Metadata.Annotations = new Dictionary<string, string>();
        }

        Metadata.Annotations[annotationName] = value;
    }

    public void AnnotateAsObjectList<TValue>(string annotationName, TValue value)
    {
        if (Metadata.Annotations is null)
        {
            Metadata.Annotations = new Dictionary<string, string>();
        }

        AnnotateAsObjectList<TValue>(Metadata.Annotations, annotationName, value);
    }

    public void SetAnnotationAsObjectList<TValue>(string annotationName, IEnumerable<TValue> list)
    {
        if (Metadata.Annotations is null)
        {
            Metadata.Annotations = new Dictionary<string, string>();
        }

        Metadata.Annotations[annotationName] = JsonSerializer.Serialize<List<TValue>>(list.ToList());
    }

    public bool TryGetAnnotationAsObjectList<TValue>(string annotationName, [NotNullWhen(true)] out List<TValue>? list)
    {
        return TryGetAnnotationAsObjectList<TValue>(Metadata.Annotations, annotationName, out list);
    }

    internal static bool TryGetAnnotationAsObjectList<TValue>(IDictionary<string, string>? annotations, string annotationName, [NotNullWhen(true)] out List<TValue>? list)
    {
        list = null;

        if (annotations is null)
        {
            return false;
        }

        string? annotationValue;
        bool found = annotations.TryGetValue(annotationName, out annotationValue);
        if (!found || string.IsNullOrWhiteSpace(annotationValue))
        {
            return false;
        }

        try
        {
            list = JsonSerializer.Deserialize<List<TValue>>(annotationValue);
        }
        catch
        {
            return false;
        }

        return list is not null;
    }

    internal static void AnnotateAsObjectList<TValue>(IDictionary<string, string> annotations, string annotationName, TValue value)
    {
        List<TValue> values;
        if (annotations.TryGetValue(annotationName, out var annotationVal) && !string.IsNullOrWhiteSpace(annotationVal))
        {
            try
            {
                values = JsonSerializer.Deserialize<List<TValue>>(annotationVal) ?? new();
            }
            catch
            {
                values = new();
            }

            if (!values.Contains(value))
            {
                values.Add(value);
            }
        }
        else
        {
            values = [value];
        }

        var newAnnotationVal = JsonSerializer.Serialize<List<TValue>>(values);
        annotations[annotationName] = newAnnotationVal;
    }
}

internal abstract class CustomResource<TSpec, TStatus> : CustomResource
{
    [JsonPropertyName("spec")]
    public TSpec Spec { get; set; }

    [JsonPropertyName("status")]
    public TStatus? Status { get; set; }

    public CustomResource(TSpec spec)
    {
        Spec = spec;
    }
}

internal sealed class CustomResourceList<T> : KubernetesObject
where T : CustomResource
{
    [JsonPropertyName("metadata")]
    public V1ListMeta Metadata { get; set; } = new V1ListMeta();

    [JsonPropertyName("items")]
    public required List<T> Items { get; set; }
}

internal sealed class EnvVar
{
    // Name of the environment variable
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    // Value of the environment variable
    [JsonPropertyName("value")]
    public string? Value { get; set; }
}

internal static class Conventions
{
    // Indicates that process ID of some process is not known
    public const int UnknownPID = -1;

    // Indicates that the exit code of some process is not known
    public const int UnknownExitCode = -1;
}

internal sealed class ServiceProducerAnnotation
{
    // Name of the service produced
    [JsonPropertyName("serviceName")]
    public string ServiceName { get; set; }

    // Desired address for the service.
    // If not set, the service must specify the placeholder for address injection via addressFor template function.
    [JsonPropertyName("address")]
    public string? Address { get; set; }

    // The port that the program will use to implement the service endpoint.
    // If not set, the service must specify the placeholder for address injection via portFor template function.
    [JsonPropertyName("port")]
    public int? Port { get; set; }

    [JsonConstructor]
    public ServiceProducerAnnotation(string serviceName)
    {
        ServiceName = serviceName;
    }

    public override bool Equals(object? obj)
    {
        if (obj is null || obj is not ServiceProducerAnnotation) { return false; }

        var other = (ServiceProducerAnnotation)obj;

        if (!string.Equals(ServiceName, other.ServiceName, StringComparison.Ordinal)) { return false; }

        // Note: string.Equals(null, null) is true, which is what we want here.
        if (!string.Equals(Address, other.Address, StringComparison.Ordinal)) { return false; }

        if (Port != other.Port) { return false; }

        return true;
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(ServiceName, Address, Port);
    }
}

internal sealed record NamespacedName(string Name, string? Namespace);

internal static class Rules
{
    public static bool IsValidObjectName(string candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return false;
        }

        if (candidate.Length > 253)
        {
            return false;
        }

        // Can only contain alphanumeric characters, hyphen, period, underscore, tilde.
        // (essentially the same as URL path characters).
        // Needs to start with a letter, underscore, or tilde.
        bool isValid = Regex.IsMatch(candidate, @"^[[a-zA-Z_~][a-zA-Z0-9\-._~]*$");
        return isValid;
    }
}

internal static class HealthStatus
{
    /// <summary>
    /// DCP considers the resource to be healthy.
    /// </summary>
    public const string Healthy = "Healthy";

    /// <summary>
    /// DCP considers the resource to be unhealthy.
    /// </summary>
    public const string Unhealthy = "Unhealthy";

    /// <summary>
    /// DCP considers the resource to be in a cautionary state (partially healthy or unknown state).
    /// </summary>
    public const string Caution = "Caution";
}

internal sealed class HealthProbe
{
    /// <summary>
    /// Name of the health probe, used to identify the results of a specific probe in the status output
    /// of the parent resource. Name must be unique for a given parent resource.
    /// </summary>
    [JsonPropertyName("name")]
    public string? Name;

    /// <summary>
    /// The type of health probe. For valid values see <see cref="HealthProbeType"/>.
    /// </summary>
    [JsonPropertyName("type")]
    public string? Type;

    /// <summary>
    /// If <see cref="HealthProbe.Type"/> is HTTP, this configures the health probe.
    /// </summary>
    [JsonPropertyName("httpProbe")]
    public HttpHealthProbeConfig? HttpProbe { get; set; }

    /// <summary>
    /// If <see cref="HealthProbe.Type"/> is Executable, this configures the health probe.
    /// </summary>
    [JsonPropertyName("executableProbe")]
    public ExecutableHealthProbeConfig? ExecutableProbe { get; set; }

    /// <summary>
    /// Determines how often the probe executes.
    /// </summary>
    [JsonPropertyName("schedule")]
    public HealthProbeSchedule? Schedule { get; set; }

    /// <summary>
    /// Optional annotations (metadata) for the health probe.
    /// </summary>
    [JsonPropertyName("annotations")]
    public IDictionary<string, string>? Annotations { get; set; }
}

internal static class HealthProbeType
{
    /// <summary>
    /// The health probe makes an HTTP request that expects a success response (i.e. 200).
    /// </summary>
    public const string Http = "HTTP";

    /// <summary>
    /// The health probe runs an executable and expects a successful exit code (0).
    /// </summary>
    public const string Executable = "Executable";

    /// <summary>
    /// The health probe executes a command in a container and expects a successful exit code (0).
    /// </summary>
    public const string ContainerExec = "ContainerExec";
}

internal sealed class HttpHealthProbeConfig
{
    /// <summary>
    /// The URL to probe.
    /// </summary>
    [JsonPropertyName("url")]
    public string? Url { get; set; }

    /// <summary>
    /// Optional collection of HTTP headers to apply to the health probe request.
    /// </summary>
    public List<HttpProbeHeader>? Headers { get; set; }
}

internal sealed class HttpProbeHeader
{
    /// <summary>
    /// The name of the header.
    /// </summary>
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    /// <summary>
    /// The (optional) value of the header.
    /// </summary>
    [JsonPropertyName("value")]
    public string? Value { get; set; }
}

internal sealed class ExecutableHealthProbeConfig
{
    // Labels to apply to probe Executable objects
    [JsonPropertyName("labels")]
    public IDictionary<string, string>? Labels { get; set; }

    // Annotations to apply to probe Executable objects
    [JsonPropertyName("annotations")]
    public IDictionary<string, string>? Annotations { get; set; }

    // Spec for the probe Executable
    [JsonPropertyName("spec")]
    public ExecutableSpec Spec { get; set; } = new ExecutableSpec();
}

internal sealed class HealthProbeSchedule
{
    /// <summary>
    /// The schedule kind for the health probe. For valid values see <see cref="HealthProbeScheduleKind"/>.
    /// </summary>
    [JsonPropertyName("kind")]
    public string? Kind { get; set; }

    /// <summary>
    /// The interval at which the health probe should be run.
    /// </summary>
    [JsonPropertyName("interval")]
    public TimeSpan? Interval { get; set; }

    /// <summary>
    /// Optional timeout for the health probe, which will mark the probe as failed if it takes longer than this duration.
    /// </summary>
    [JsonPropertyName("timeout")]
    public TimeSpan? Timeout { get; set; }

    /// <summary>
    /// Optional initial delay before the health probe starts running.
    /// </summary>
    [JsonPropertyName("initialDelay")]
    public TimeSpan? InitialDelay { get; set; }
}

internal static class HealthProbeScheduleKind
{
    /// <summary>
    /// The health probe is run continuously (based on the configured schedule).
    /// </summary>
    public const string Continuous = "Continuous";

    /// <summary>
    /// The health probe is run based on the configured schedule until it succeeds once, at
    /// which point it is considered successful for the remainder of the parent object's lifetime.
    /// This is useful for expensive startup probes such as checking for a database to be ready to
    /// accept requests.
    /// </summary>
    public const string UntilSuccess = "UntilSuccess";
}

internal sealed class HealthProbeResult
{
    /// <summary>
    /// The outcome of the health probe (success, failure, etc.)
    /// </summary>
    [JsonPropertyName("outcome")]
    public string? Outcome { get; set;}

    /// <summary>
    /// The timestamp when the health probe result was determined.
    /// </summary>
    [JsonPropertyName("timestamp")]
    public DateTime? Timestamp { get; set; }

    /// <summary>
    /// The name of the probe that generated this result. Corresponds to a specific <see cref="HealthProbe"/>
    /// configured in the spec of the parent resource.
    /// </summary>
    [JsonPropertyName("probeName")]
    public string? ProbeName { get; set; }

    /// <summary>
    /// Optional human-readable message describing the probe outcome.
    /// </summary>
    [JsonPropertyName("reason")]
    public string? Reason { get; set; }
}

internal static class HealthProbeOutcome
{
    /// <summary>
    /// The health probe was successful.
    /// </summary>
    public const string Success = "Success";

    /// <summary>
    /// The health probe failed.
    /// </summary>
    public const string Failure = "Failure";

    /// <summary>
    /// The health probe hasn't succeeded or failed yet.
    /// </summary>
    public const string Unknown = "Unknown";
}

internal static class Logs
{
    public const string StreamTypeStartupStdOut = "startup_stdout";
    public const string StreamTypeStartupStdErr = "startup_stderr";
    public const string StreamTypeStdOut = "stdout";
    public const string StreamTypeStdErr = "stderr";
    public const string StreamTypeSystem = "system";
    public const string SubResourceName = "log";
}
