// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace NativeIntegrations.DockerCompose;

public sealed record ComposeBuild(string Context, string Dockerfile);
public sealed record ComposeHealthcheck(string[] Test, string? Interval = null, string? Timeout = null, int? Retries = null);
public sealed record ComposePort(string Endpoint, int? Published = null, string HostIp = "127.0.0.1");

public sealed class DockerComposeServiceOptions
{
    public string? Image { get; init; }
    public ComposeBuild? Build { get; init; }
    public ComposeHealthcheck? Healthcheck { get; init; }
    public ComposePort[]? Ports { get; init; }
    public bool Exclude { get; init; }
    public bool ReplaceRunLifecycle { get; init; }

    internal JsonObject ToJson()
    {
        var result = new JsonObject();
        if (Image is not null)
        {
            result["image"] = Image;
        }
        if (Build is not null)
        {
            result["build"] = new JsonObject { ["context"] = Build.Context, ["dockerfile"] = Build.Dockerfile };
        }
        if (Healthcheck is not null)
        {
            result["healthcheck"] = ComposeJson.Healthcheck(Healthcheck);
        }
        if (Ports is not null)
        {
            result["ports"] = new JsonArray(Ports.Select(port =>
            {
                var exposure = new JsonObject { ["endpoint"] = port.Endpoint, ["hostIp"] = port.HostIp };
                if (port.Published is { } published)
                {
                    exposure["published"] = published;
                }

                return (JsonNode)exposure;
            }).ToArray());
        }
        if (Exclude)
        {
            result["exclude"] = true;
        }
        if (ReplaceRunLifecycle)
        {
            result["replaceRunLifecycle"] = true;
        }

        return result;
    }
}

public sealed record ComposePublishedPort(string Endpoint, int Target, string Protocol, string Scheme, string HostIp, int? Published);
public sealed record ComposePlannedService(string Resource, string Service, bool Healthcheck, IReadOnlyList<ComposePublishedPort> Ports);
public sealed record DockerComposePlan(JsonObject Compose, JsonObject Inputs, List<ComposePlannedService> Services);

/// <summary>A typed customization view over the generated Compose service, not a second application model.</summary>
public sealed class ComposeServiceBuilder
{
    private readonly string _applicationDirectory;
    private readonly Func<string, string> _render;
    public JsonObject Model { get; }

    internal ComposeServiceBuilder(JsonObject model, string applicationDirectory, Func<string, string>? render = null)
    {
        Model = model;
        _applicationDirectory = applicationDirectory;
        _render = render ?? ComposeJson.Literal;
    }

    public string? Image
    {
        get => Model["image"]?.GetValue<string>();
        set
        {
            if (value is not null)
            {
                ComposeJson.ValidateImage(value);
            }
            Set("image", value is null ? null : ComposeJson.Literal(value));
        }
    }

    public ComposeBuild? Build
    {
        get => Model["build"] is JsonObject build ? new(build["context"]!.GetValue<string>(), build["dockerfile"]!.GetValue<string>()) : null;
        set => Set("build", value is null ? null : ComposeJson.BuildPaths(value, _applicationDirectory));
    }

    public ComposeHealthcheck? Healthcheck
    {
        get => Model["healthcheck"] is JsonObject health ? ComposeJson.ReadHealthcheck(health) : null;
        set => Set("healthcheck", value is null ? null : ComposeJson.Healthcheck(value, escape: true));
    }

    public string? ContainerName
    {
        get => Model["container_name"]?.GetValue<string>();
        set => Set("container_name", value is null ? null : _render(ComposeJson.Text(value)));
    }

    public string? Restart
    {
        get => Model["restart"]?.GetValue<string>();
        set
        {
            if (value is not null && !Regex.IsMatch(value, "^(no|always|unless-stopped|on-failure(?::[1-9][0-9]*)?)$", RegexOptions.CultureInvariant))
            {
                throw new ArgumentException("Unsupported Compose restart policy.", nameof(value));
            }
            Set("restart", value);
        }
    }

    public string? PullPolicy
    {
        get => Model["pull_policy"]?.GetValue<string>();
        set
        {
            if (value is not null && !Regex.IsMatch(value, "^(always|never|missing|build|daily|weekly|every_[0-9]+[hms])$", RegexOptions.CultureInvariant))
            {
                throw new ArgumentException("Unsupported Compose pull policy.", nameof(value));
            }
            Set("pull_policy", value);
        }
    }

    public void SetLabel(string name, string value) => Model["labels"]!.AsObject()[name] = ComposeJson.Literal(value);

    public void AddVolume(string source, string target, bool isReadOnly = false)
    {
        ComposeJson.Key(source);
        ComposeJson.AbsoluteTarget(target);
        (Model["volumes"] ??= new JsonArray()).AsArray().Add(new JsonObject
        {
            ["type"] = "volume", ["source"] = source, ["target"] = ComposeJson.Literal(target), ["read_only"] = isReadOnly
        });
    }

    private void Set(string key, JsonNode? value)
    {
        if (value is null)
        {
            Model.Remove(key);
        }
        else
        {
            Model[key] = value;
        }
    }
}

public sealed class ComposeFileBuilder(JsonObject model, string applicationDirectory)
{
    public JsonObject Model { get; } = model;
    public string Name
    {
        get => Model["name"]!.GetValue<string>();
        set
        {
            if (!Regex.IsMatch(value, "^[a-z0-9][a-z0-9_-]*$", RegexOptions.CultureInvariant))
            {
                throw new ArgumentException("Invalid Compose project name.", nameof(value));
            }
            Model["name"] = value;
        }
    }

    public ComposeServiceBuilder GetService(string name) => new(
        Model["services"]![name]?.AsObject() ?? throw new InvalidOperationException($"Compose service '{name}' does not exist."), applicationDirectory);

    public ComposeServiceBuilder AddService(string name, string image, IReadOnlyDictionary<string, string>? environment = null)
    {
        ComposeJson.Key(name);
        ComposeJson.ValidateImage(image);
        var services = Model["services"]!.AsObject();
        if (services.ContainsKey(name))
        {
            throw new ArgumentException($"Duplicate Compose service '{name}'.", nameof(name));
        }
        var service = new JsonObject { ["image"] = ComposeJson.Literal(image), ["labels"] = new JsonObject() };
        if (environment is not null)
        {
            service["environment"] = new JsonObject(environment.Select(pair =>
                KeyValuePair.Create(pair.Key, (JsonNode?)JsonValue.Create(ComposeJson.Literal(pair.Value)))));
        }
        services.Add(name, service);

        return new(service, applicationDirectory);
    }

    public void AddNetwork(string name, JsonObject options) => Add("networks", name, ComposeJson.Escape(options));
    public void AddVolume(string name, string? driver = null, IReadOnlyDictionary<string, string>? labels = null)
    {
        var volume = new JsonObject();
        if (driver is not null)
        {
            volume["driver"] = ComposeJson.Literal(driver);
        }
        if (labels is not null)
        {
            volume["labels"] = new JsonObject(labels.Select(pair =>
                KeyValuePair.Create(pair.Key, (JsonNode?)JsonValue.Create(ComposeJson.Literal(pair.Value)))));
        }
        Add("volumes", name, volume);
    }
    public void AddConfig(string name, string content) => Add("configs", name, new() { ["content"] = ComposeJson.Literal(content) });
    public void AddSecret(string name, string? externalName = null)
    {
        var secret = new JsonObject { ["external"] = true };
        if (externalName is not null)
        {
            secret["name"] = ComposeJson.Literal(externalName);
        }
        Add("secrets", name, secret);
    }
    private void Add(string collection, string name, JsonObject value)
    {
        ComposeJson.Key(name);
        (Model[collection] ??= new JsonObject()).AsObject()[name] = value;
    }
}

public sealed class CapturedEnvironmentVariable(JsonObject model)
{
    public string? Description
    {
        get => model["description"]?.GetValue<string>();
        set => model["description"] = value;
    }
    public string? DefaultValue
    {
        get => model["defaultValue"]?.GetValue<string>();
        set
        {
            if (value is not null && model["secret"]!.GetValue<bool>())
            {
                throw new InvalidOperationException("Secret defaults cannot be written to deployment artifacts.");
            }
            if (value is null)
            {
                model.Remove("defaultValue");
            }
            else
            {
                model["defaultValue"] = ComposeJson.Text(value, allowEmpty: true);
            }
        }
    }
}

internal static class ComposeJson
{
    internal static readonly JsonSerializerOptions s_indented = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    internal static string Json(JsonNode node) => node.ToJsonString(s_indented) + "\n";
    internal static string Literal(string value) => value.Replace("$", "$$", StringComparison.Ordinal);
    internal static string Text(string value, bool allowEmpty = false)
    {
        if ((!allowEmpty && string.IsNullOrWhiteSpace(value)) || value.Contains('\0'))
        {
            throw new ArgumentException("Expected text without NUL characters.", nameof(value));
        }

        return value;
    }
    internal static void Key(string value)
    {
        if (!Regex.IsMatch(value, "^[a-zA-Z0-9][a-zA-Z0-9_.-]*$", RegexOptions.CultureInvariant))
        {
            throw new ArgumentException("Invalid Compose identifier.", nameof(value));
        }
    }
    internal static void AbsoluteTarget(string value)
    {
        Text(value);
        if (!value.StartsWith('/'))
        {
            throw new ArgumentException("Compose mount targets must be absolute.", nameof(value));
        }
    }
    internal static void ValidateImage(string value)
    {
        if (!Regex.IsMatch(value, "^[a-zA-Z0-9][a-zA-Z0-9._/:@-]*$", RegexOptions.CultureInvariant))
        {
            throw new ArgumentException("Deployment image must be a literal image reference.", nameof(value));
        }
    }
    internal static void Port(int value)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value, 65535);
    }
    internal static JsonObject BuildPaths(ComposeBuild build, string applicationDirectory)
    {
        var context = Path.GetFullPath(Text(build.Context), applicationDirectory);
        var dockerfile = Path.GetFullPath(Text(build.Dockerfile), applicationDirectory);
        var within = Path.GetRelativePath(context, dockerfile);
        if (within is "." or ".." || within.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) || Path.IsPathRooted(within))
        {
            throw new ArgumentException("Dockerfile must be inside its explicit build context.", nameof(build));
        }

        return new() { ["context"] = Literal(context), ["dockerfile"] = Literal(dockerfile) };
    }
    internal static JsonObject Healthcheck(ComposeHealthcheck value, bool escape = false)
    {
        if (value.Test.Length < 2 || value.Test[0] is not ("CMD" or "CMD-SHELL") ||
            value.Test[0] == "CMD-SHELL" && value.Test.Length != 2)
        {
            throw new ArgumentException("Healthcheck must start with CMD or CMD-SHELL and contain a command.", nameof(value));
        }
        var result = new JsonObject { ["test"] = Strings(value.Test.Select(part => escape ? Literal(Text(part, true)) : Text(part, true))) };
        foreach (var (key, duration) in new[] { ("interval", value.Interval), ("timeout", value.Timeout) })
        {
            if (duration is not null)
            {
                if (!Regex.IsMatch(duration, @"^(?:[0-9]+(?:\.[0-9]+)?(?:h|ms|us|s|m))+$", RegexOptions.CultureInvariant) ||
                    !duration.Any(character => character is >= '1' and <= '9'))
                {
                    throw new ArgumentException("Healthcheck durations must be positive Compose durations.", nameof(value));
                }
                result[key] = duration;
            }
        }
        if (value.Retries is { } retries)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(retries, 1);
            result["retries"] = retries;
        }

        return result;
    }
    internal static ComposeHealthcheck ReadHealthcheck(JsonObject value) => new(
        value["test"]!.AsArray().Select(part => part!.GetValue<string>()).ToArray(),
        value["interval"]?.GetValue<string>(), value["timeout"]?.GetValue<string>(), value["retries"]?.GetValue<int>());
    internal static JsonArray Strings(IEnumerable<string> values) => new(values.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray());
    internal static JsonObject Escape(JsonObject source) => EscapeNode(source)!.AsObject();
    private static JsonNode? EscapeNode(JsonNode? node) => node switch
    {
        JsonObject obj => new JsonObject(obj.Select(pair => KeyValuePair.Create(pair.Key, EscapeNode(pair.Value)))),
        JsonArray array => new JsonArray(array.Select(EscapeNode).ToArray()),
        JsonValue value when value.TryGetValue<string>(out var text) => JsonValue.Create(Literal(text)),
        _ => node?.DeepClone()
    };
}
