// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Serialization;

namespace Aspire.Hosting.Dcp.Model.V2;

internal sealed class PhysicalContainerImageConfig
{
    [JsonPropertyName("image")]
    public string? Image { get; set; }

    [JsonPropertyName("build")]
    public ContainerBuildContext? Build { get; set; }

    // Omission uses DCP's missing pull policy and ifNeeded build policy.
    [JsonPropertyName("pullPolicy")]
    public string? PullPolicy { get; set; }

    [JsonPropertyName("buildPolicy")]
    public string? BuildPolicy { get; set; }

    // Null uses DCP's retry default; zero disables retries.
    [JsonPropertyName("pullRetryLimit")]
    public int? PullRetryLimit { get; set; }
}

internal sealed class ContainerBuildContext
{
    [JsonPropertyName("context")]
    public string? Context { get; set; }

    [JsonPropertyName("contextArchive")]
    public ContainerBuildContextArchive? ContextArchive { get; set; }

    [JsonPropertyName("digest")]
    public string? Digest { get; set; }

    [JsonPropertyName("dockerfile")]
    public string? Dockerfile { get; set; }

    [JsonPropertyName("tags")]
    public List<string>? Tags { get; set; }

    [JsonPropertyName("args")]
    public List<EnvVar>? Args { get; set; }

    [JsonPropertyName("secrets")]
    public List<ContainerBuildSecret>? Secrets { get; set; }

    [JsonPropertyName("stage")]
    public string? Stage { get; set; }

    [JsonPropertyName("labels")]
    public List<Label>? Labels { get; set; }

    [JsonPropertyName("platform")]
    public string? Platform { get; set; }

    [JsonPropertyName("baseImages")]
    public List<string>? BaseImages { get; set; }
}

internal sealed class ContainerBuildContextArchive
{
    [JsonPropertyName("source")]
    public string? Source { get; set; }

    [JsonPropertyName("sha256")]
    public string? Sha256 { get; set; }

    [JsonPropertyName("rawContents")]
    public string? RawContents { get; set; }
}

internal sealed class ContainerBuildSecret
{
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("source")]
    public string? Source { get; set; }

    [JsonPropertyName("value")]
    public string? Value { get; set; }
}

internal static class BuildSecretType
{
    public const string Env = "env";
    public const string File = "file";
}

internal static class ImagePullPolicy
{
    public const string Always = "always";
    public const string BestEffort = "bestEffort";
    public const string Missing = "missing";
    public const string Never = "never";
}

internal static class ImageBuildPolicy
{
    public const string IfNeeded = "ifNeeded";
    public const string Always = "always";
}

internal sealed class VolumeMount
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "";

    [JsonPropertyName("source")]
    public string? Source { get; set; }

    [JsonPropertyName("volumeRef")]
    public string? VolumeRef { get; set; }

    [JsonPropertyName("target")]
    public string Target { get; set; } = "";

    [JsonPropertyName("readOnly")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool ReadOnly { get; set; }
}

internal static class VolumeMountType
{
    public const string Bind = "bind";
    public const string Volume = "volume";
}

internal sealed class CreateFileSystem
{
    [JsonPropertyName("destination")]
    public string Destination { get; set; } = "";

    [JsonPropertyName("defaultOwner")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int DefaultOwner { get; set; }

    [JsonPropertyName("defaultGroup")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int DefaultGroup { get; set; }

    // Go fs.FileMode is a uint32 number, not an octal string. A zero pointer differs from omission.
    [JsonPropertyName("umask")]
    public uint? Umask { get; set; }

    [JsonPropertyName("entries")]
    public List<FileSystemEntry>? Entries { get; set; }
}

internal sealed class FileSystemEntry
{
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("owner")]
    public int? Owner { get; set; }

    [JsonPropertyName("group")]
    public int? Group { get; set; }

    [JsonPropertyName("mode")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public uint Mode { get; set; }

    [JsonPropertyName("source")]
    public string? Source { get; set; }

    [JsonPropertyName("contents")]
    public string? Contents { get; set; }

    [JsonPropertyName("rawContents")]
    public string? RawContents { get; set; }

    [JsonPropertyName("continueOnError")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool ContinueOnError { get; set; }

    [JsonPropertyName("entries")]
    public List<FileSystemEntry>? Entries { get; set; }
}

internal static class FileSystemEntryType
{
    public const string File = "file";
    public const string OpenSSL = "openssl";
    public const string Directory = "directory";
}
