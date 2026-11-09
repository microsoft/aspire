// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Serialization;
using k8s.Models;

namespace Aspire.Hosting.Dcp.Model.V2;

internal sealed class NamespaceStatus
{
    [JsonPropertyName("phase")]
    public string? Phase { get; set; }

    [JsonPropertyName("conditions")]
    public List<V1Condition>? Conditions { get; set; }
}

internal static class NamespacePhase
{
    public const string Active = "Active";
    public const string Terminating = "Terminating";
}

internal sealed class Namespace : CustomResource, IKubernetesStaticMetadata
{
    public static string ObjectKind => "Namespace";

    public const string WorkloadIdAnnotation = Model.Dcp.GroupName + "/workload-id";

    [JsonPropertyName("status")]
    public NamespaceStatus? Status { get; set; }

    public static Namespace Create(string name)
    {
        return new()
        {
            Kind = ObjectKind,
            ApiVersion = Dcp.GroupVersion.ToString(),
            Metadata = new() { Name = name }
        };
    }
}
