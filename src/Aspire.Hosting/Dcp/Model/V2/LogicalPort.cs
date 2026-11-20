// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Serialization;

namespace Aspire.Hosting.Dcp.Model.V2;

internal class LogicalServiceBinding
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("serviceRef")]
    public string? ServiceRef { get; set; }

    [JsonPropertyName("service")]
    public LogicalServiceTemplate? Service { get; set; }
}

internal sealed class LogicalServiceTemplate : LogicalServiceSpec
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";
}

internal sealed class LogicalContainerPort : LogicalServiceBinding
{
    [JsonPropertyName("containerPort")]
    public int ContainerPort { get; set; }

    [JsonPropertyName("hostPort")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int HostPort { get; set; }

    [JsonPropertyName("hostIP")]
    public string? HostIp { get; set; }

    [JsonPropertyName("address")]
    public string? Address { get; set; }
}

internal sealed class LogicalProcessPort : LogicalServiceBinding
{
    [JsonPropertyName("address")]
    public string? Address { get; set; }

    // Zero asks DCP to allocate the process port.
    [JsonPropertyName("port")]
    public int Port { get; set; }
}

internal sealed class LogicalPortStatus
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("serviceRef")]
    public string ServiceRef { get; set; } = "";

    [JsonPropertyName("address")]
    public string Address { get; set; } = "";

    [JsonPropertyName("port")]
    public int Port { get; set; }
}

internal sealed class PhysicalContainerPortMapping
{
    [JsonPropertyName("containerPort")]
    public int ContainerPort { get; set; }

    [JsonPropertyName("protocol")]
    public string? Protocol { get; set; }

    [JsonPropertyName("hostIP")]
    public string? HostIp { get; set; }

    [JsonPropertyName("hostPort")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int HostPort { get; set; }
}

internal sealed class ContainerPortSpec
{
    [JsonPropertyName("hostPort")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int HostPort { get; set; }

    [JsonPropertyName("containerPort")]
    public int ContainerPort { get; set; }

    // Omission means one port, as opposed to a range.
    [JsonPropertyName("rangeSize")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int RangeSize { get; set; }

    [JsonPropertyName("protocol")]
    public string? Protocol { get; set; }

    [JsonPropertyName("hostIP")]
    public string? HostIp { get; set; }
}
