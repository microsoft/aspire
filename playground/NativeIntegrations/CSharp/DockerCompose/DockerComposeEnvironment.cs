// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Aspire.Hosting.Native.Authoring;
using static NativeIntegrations.DockerCompose.DockerComposeGenerator;

namespace NativeIntegrations.DockerCompose;

public sealed class DockerComposeEnvironmentOptions
{
    public DockerCommandRunner Runner { get; init; } = DockerCommand.RunAsync;
    public int WaitTimeoutSeconds { get; init; } = 120;
    public IReadOnlyDictionary<string, string?>? ParameterEnvironment { get; init; }
}

/// <summary>Experimental existing-target deployer. Docker, registry access and destructive cleanup are external.</summary>
public sealed class DockerComposeEnvironment
{
    private readonly DockerComposeEnvironmentOptions _options;
    private readonly IResourceBuilder<DockerComposeEnvironmentResource>? _builder;
    private readonly ComposeAuthoringState? _authoring;
    private readonly ConditionalWeakTable<JsonObject, Lazy<Task<DockerComposePlan>>> _plans = new();
    private DockerCommandRunner _runner;
    public string Name { get; }

    public DockerComposeEnvironment(string name, DockerComposeEnvironmentOptions? options = null) : this(name, options ?? new(), null, null)
    {
    }

    internal DockerComposeEnvironment(string name, DockerComposeEnvironmentOptions options,
        IResourceBuilder<DockerComposeEnvironmentResource>? builder, ComposeAuthoringState? authoring)
    {
        Name = ComposeJson.Text(name);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.WaitTimeoutSeconds, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(options.WaitTimeoutSeconds, 3600);
        _options = options;
        _runner = options.Runner;
        _builder = builder;
        _authoring = authoring;
    }

    public DockerComposeEnvironment WithRunner(DockerCommandRunner runner)
    {
        _runner = runner;

        return this;
    }

    public async Task<DockerComposePlan> PlanAsync(PublishingContext context)
    {
        if (_builder is null)
        {
            return Generate(context, Name);
        }
        try
        {
            return await _plans.GetValue(context.Model, _ => new(() => CustomizeAsync(context))).Value.ConfigureAwait(false);
        }
        catch
        {
            // Failed callbacks must not poison retries with a permanently cached failed task.
            _plans.Remove(context.Model);
            throw;
        }
    }

    private async Task<DockerComposePlan> CustomizeAsync(PublishingContext context)
    {
        var callbacks = _authoring!.ServiceCallbacks;
        var plan = Generate(context, Name, callbacks.Keys.Select(resource => resource.Name).ToHashSet(StringComparer.Ordinal));
        foreach (var entry in plan.Services)
        {
            var resource = _builder!.ApplicationBuilder.Resources.SingleOrDefault(resource => resource.Name == entry.Resource);
            if (resource is null || !callbacks.TryGetValue(resource, out var customizations))
            {
                continue;
            }
            var target = new DockerComposeServiceResource(entry.Resource, _builder, plan, context);
            var service = new ComposeServiceBuilder(plan.Compose["services"]![entry.Service]!.AsObject(), context.ApplicationDirectory, value =>
            {
                // Only placeholders captured through Parameter() are interpolated. Other
                // authored "${HOME}" and "$PASSWORD" strings must remain literal to Compose.
                var placeholders = Objects(plan.Inputs["parameters"]).Select(parameter => Placeholder(Text(parameter, "environmentVariable"))).ToHashSet(StringComparer.Ordinal);

                return string.Concat(Regex.Split(value, @"(\$\{[^}]*\})", RegexOptions.CultureInvariant)
                    .Select(part => placeholders.Contains(part) ? part : ComposeJson.Literal(part)));
            });
            foreach (var customize in customizations)
            {
                context.CancellationToken.ThrowIfCancellationRequested();
                await customize(target, service).ConfigureAwait(false);
            }
        }
        var file = new ComposeFileBuilder(plan.Compose, context.ApplicationDirectory);
        var originalName = file.Name;
        foreach (var configure in _builder!.Resource.FileCallbacks)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            await configure(file).ConfigureAwait(false);
        }
        if (file.Name != originalName && Objects(context.Model["resources"]).Count(resource => Text(resource, "type") == "docker-compose/environment") > 1)
        {
            file.Name += "-" + Hash(originalName);
        }
        plan.Inputs["projectName"] = file.Name;
        for (var index = 0; index < plan.Services.Count; index++)
        {
            var entry = plan.Services[index];
            var service = plan.Compose["services"]![entry.Service]!.AsObject();
            var native = Objects(context.Model["resources"]).SingleOrDefault(resource => Text(resource, "name") == entry.Resource);
            if (native is not null)
            {
                var options = Options(native);
                if (string.IsNullOrWhiteSpace(service["image"]?.GetValue<string>()) ||
                    Text(native, "kind") == "executable" && service["build"] is null)
                {
                    throw new InvalidOperationException($"Resource '{entry.Resource}' requires a deployment image and explicit executable build metadata.");
                }
                if (Strings(native["lifecycle"]).Any() && !Boolean(options, "replaceRunLifecycle") ||
                    (Strings(native["lifecycle"]).Any() || Boolean(options, "replaceRunLifecycle")) && service["healthcheck"] is null)
                {
                    throw new InvalidOperationException($"Resource '{entry.Resource}' must explicitly replace host lifecycle with a target healthcheck or be excluded.");
                }
            }
            plan.Services[index] = entry with { Healthcheck = service["healthcheck"] is not null };
        }
        foreach (var (name, node) in plan.Compose["services"]!.AsObject())
        {
            var service = node!.AsObject();
            ComposeJson.ValidateImage(Text(service, "image"));
            if (service["healthcheck"] is JsonObject healthcheck)
            {
                ComposeJson.Healthcheck(ComposeJson.ReadHealthcheck(healthcheck));
            }
            if (service["depends_on"] is JsonObject dependencies)
            {
                foreach (var dependency in dependencies.Select(pair => pair.Key).ToArray())
                {
                    var producer = plan.Compose["services"]![dependency]?.AsObject() ??
                        throw new InvalidOperationException($"Service '{name}' references missing Compose service '{dependency}'.");
                    dependencies[dependency] = new JsonObject { ["condition"] = producer["healthcheck"] is null ? "service_started" : "service_healthy" };
                }
            }
            if (!plan.Services.Any(entry => entry.Service == name))
            {
                plan.Services.Add(new(name, name, service["healthcheck"] is not null, []));
            }
            foreach (var mount in service["volumes"] is null ? [] : Objects(service["volumes"]))
            {
                if (Text(mount, "type") == "volume" && plan.Compose["volumes"]?[Text(mount, "source")] is null)
                {
                    throw new InvalidOperationException($"Service '{name}' references missing Compose volume '{Text(mount, "source")}'.");
                }
            }
        }
        CaptureParameters(plan, context.Model);
        var captured = (plan.Inputs["environment"]?.AsObject() ?? []).ToDictionary(pair => pair.Key,
            pair => new CapturedEnvironmentVariable(pair.Value!.AsObject()), StringComparer.Ordinal);
        foreach (var configure in _builder.Resource.EnvironmentCallbacks)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            configure(captured);
        }

        return plan;
    }

    public async Task PublishAsync(PublishingContext context)
    {
        context = Scoped(context);
        context.CancellationToken.ThrowIfCancellationRequested();
        var plan = await PlanAsync(context).ConfigureAwait(false);
        foreach (var service in plan.Compose["services"]!.AsObject().Select(pair => pair.Value!.AsObject()))
        {
            if (service["build"] is JsonObject build &&
                (!Directory.Exists(Text(build, "context").Replace("$$", "$", StringComparison.Ordinal)) ||
                 !File.Exists(Text(build, "dockerfile").Replace("$$", "$", StringComparison.Ordinal))))
            {
                throw new InvalidOperationException("Build context must be an existing directory with an existing Dockerfile.");
            }
        }
        context.CancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(context.OutputPath);
        await File.WriteAllTextAsync(Path.Combine(context.OutputPath, "compose.yaml"), ComposeJson.Json(plan.Compose), context.CancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(context.OutputPath, "deployment-inputs.json"), ComposeJson.Json(plan.Inputs), context.CancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(context.OutputPath, "prerequisites.txt"), string.Join('\n',
            "Docker Compose v2.20+ and a running Docker daemon are external prerequisites.",
            "compose.yaml contains JSON, which is valid YAML. Publish never executes Docker.",
            "Build contexts and Dockerfiles are absolute local paths; artifacts are not portable to another machine.",
            "Review Dockerfile and .dockerignore. No context is copied by this publisher.",
            "Explicitly exposed ports use loopback and random host ports by default.",
            "Named volumes are project-scoped and retained; native-run volumes are not reused.",
            "Supply ASPIRE_PARAMETER_* values explicitly. Native local secrets are never used.",
            "Inputs stay in memory, not .env files. Secrets remain visible to Docker/container administrators.",
            "Docker credentials, contexts, registry login and image cleanup are managed externally.",
            "No automatic rollback, orphan removal or destructive cleanup.",
            "External cleanup (argument list): " + new JsonArray("docker", "compose", "-p", Text(plan.Compose, "name"), "-f",
                Path.Combine(context.OutputPath, "compose.yaml"), "down").ToJsonString(),
            "Do not add --volumes unless destruction of project data is intended.", ""), context.CancellationToken).ConfigureAwait(false);
    }

    public async Task DeployAsync(PublishingContext context)
    {
        context = Scoped(context);
        context.CancellationToken.ThrowIfCancellationRequested();
        var plan = await PlanAsync(context).ConfigureAwait(false);
        var composePath = Path.Combine(context.OutputPath, "compose.yaml");
        foreach (var (path, expected) in new[] { (composePath, plan.Compose), (Path.Combine(context.OutputPath, "deployment-inputs.json"), plan.Inputs) })
        {
            if (!File.Exists(path) || !JsonNode.DeepEquals(JsonNode.Parse(await File.ReadAllTextAsync(path, context.CancellationToken).ConfigureAwait(false)), expected))
            {
                throw new InvalidOperationException("Deployment artifacts are missing or stale; publish this model before deploying.");
            }
        }
        var environment = ProcessEnvironment();
        var values = ResolveInputs(plan, context.Model, _options.ParameterEnvironment ?? environment);
        var toolEnvironment = environment.Where(pair => pair.Value is not null && Regex.IsMatch(pair.Key,
            "^(PATH|HOME|USERPROFILE|SystemRoot|TEMP|TMP|DOCKER_[A-Z0-9_]+|SSH_AUTH_SOCK|XDG_RUNTIME_DIR|HTTP_PROXY|HTTPS_PROXY|NO_PROXY)$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)).ToDictionary(pair => pair.Key, pair => pair.Value!, StringComparer.Ordinal);
        foreach (var (key, value) in values)
        {
            toolEnvironment[key] = value;
        }
        toolEnvironment["COMPOSE_DISABLE_ENV_FILE"] = "1";
        var secrets = toolEnvironment.Values.Where(value => value.Length > 0).ToArray();
        var version = (await InvokeAsync(["compose", "version", "--short"], "Compose preflight").ConfigureAwait(false)).Trim();
        var match = Regex.Match(version, @"^v?(\d+)\.(\d+)(?:\.\d+)?(?:[-+][a-zA-Z0-9.-]+)?$", RegexOptions.CultureInvariant);
        if (!match.Success || int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) < 2 ||
            match.Groups[1].Value == "2" && int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture) < 20)
        {
            throw new InvalidOperationException("Docker Compose v2.20 or newer is required for bounded --wait deployment.");
        }
        await InvokeAsync(["info", "--format", "{{json .ServerVersion}}"], "daemon preflight").ConfigureAwait(false);
        string[] prefix = ["compose", "--ansi", "never", "--env-file", OperatingSystem.IsWindows() ? "NUL" : "/dev/null",
            "-p", Text(plan.Compose, "name"), "-f", composePath];
        await InvokeAsync([.. prefix, "config", "--quiet"], "Compose validation").ConfigureAwait(false);
        await InvokeAsync([.. prefix, "up", "--build", "--detach", "--wait", "--wait-timeout",
            _options.WaitTimeoutSeconds.ToString(CultureInfo.InvariantCulture)], "Compose apply", _options.WaitTimeoutSeconds + 120).ConfigureAwait(false);
        var state = QueryState(plan, await InvokeAsync([.. prefix, "ps", "--all", "--format", "json"], "Compose status").ConfigureAwait(false));
        context.CancellationToken.ThrowIfCancellationRequested();
        await File.WriteAllTextAsync(Path.Combine(context.OutputPath, "deployment-state.json"), ComposeJson.Json(state), context.CancellationToken).ConfigureAwait(false);

        async Task<string> InvokeAsync(string[] arguments, string operation, int timeoutSeconds = 30)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            DockerCommandResult result;
            try
            {
                result = await _runner("docker", arguments, new(context.OutputPath, toolEnvironment,
                    TimeSpan.FromSeconds(timeoutSeconds), context.CancellationToken)).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // Injectable runners and process exceptions can include credentials.
                // Keep the operation actionable without exposing their raw payload.
                throw new InvalidOperationException($"Docker {operation} could not complete. Check Docker availability, timeout, and cancellation.");
            }
            if (Encoding.UTF8.GetByteCount(result.Stdout) + Encoding.UTF8.GetByteCount(result.Stderr) > 1024 * 1024)
            {
                throw new InvalidOperationException($"Docker {operation} exceeded the bounded output limit.");
            }
            if (result.ExitCode != 0)
            {
                throw new InvalidOperationException($"Docker {operation} failed (exit {result.ExitCode}): {Redact(result.Stdout + "\n" + result.Stderr, secrets)}");
            }

            return result.Stdout;
        }
    }

    private PublishingContext Scoped(PublishingContext context) =>
        Objects(context.Model["resources"]).Count(resource => Text(resource, "type") == "docker-compose/environment") > 1 ?
            context with { OutputPath = Path.Combine(context.OutputPath, ServiceName(Name)) } : context;

    private static IReadOnlyDictionary<string, string?> ProcessEnvironment() => Environment.GetEnvironmentVariables().Cast<DictionaryEntry>()
        .ToDictionary(entry => (string)entry.Key, entry => (string?)entry.Value, StringComparer.Ordinal);

    private static string Redact(string value, IEnumerable<string> secrets)
    {
        var representations = secrets.SelectMany(secret => new[]
        {
            secret, EncodeUri(secret), Uri.EscapeDataString(secret),
            JsonValue.Create(secret)!.ToJsonString(ComposeJson.s_indented)[1..^1], JsonValue.Create(secret)!.ToJsonString()[1..^1]
        }).Where(secret => secret.Length > 0).Distinct().OrderByDescending(secret => secret.Length);
        foreach (var secret in representations)
        {
            value = value.Replace(secret, "[REDACTED]", StringComparison.Ordinal);
        }

        return Regex.Replace(value[..Math.Min(value.Length, 4096)], "[\\x00-\\x08\\x0b\\x0c\\x0e-\\x1f\\x7f\\x1b]", "", RegexOptions.CultureInvariant);
    }

    private static JsonObject QueryState(DockerComposePlan plan, string output)
    {
        JsonObject[] rows;
        try
        {
            // Compose ps emits an array, or NDJSON with the same row shape:
            // {"Service":"api","State":"running","Health":"healthy","Publishers":[
            // {"URL":"127.0.0.1","TargetPort":8080,"PublishedPort":49152,"Protocol":"tcp"}]}
            var source = output.Trim();
            rows = source.StartsWith('[') ? Objects(JsonNode.Parse(source)).ToArray() :
                source.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => JsonNode.Parse(line)!.AsObject()).ToArray();
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or ArgumentException)
        {
            throw new InvalidOperationException("Docker Compose returned invalid service status JSON.");
        }
        var services = new JsonArray();
        foreach (var expected in plan.Services)
        {
            var matches = rows.Where(row => row["Service"]?.GetValue<string>() == expected.Service).ToArray();
            if (matches.Length != 1 || matches[0]["State"]?.GetValue<string>() != "running" ||
                expected.Healthcheck && matches[0]["Health"]?.GetValue<string>() != "healthy" ||
                !expected.Healthcheck && matches[0]["Health"]?.GetValue<string>() is not (null or "" or "healthy"))
            {
                throw new InvalidOperationException($"Docker Compose service '{expected.Resource}' is missing, not running, or not healthy.");
            }
            var row = matches[0];
            var endpoints = new JsonArray();
            foreach (var exposure in expected.Ports)
            {
                var publishers = row["Publishers"] is null ? [] : Objects(row["Publishers"]).Where(publisher =>
                    publisher["TargetPort"]?.GetValue<int>() == exposure.Target && publisher["Protocol"]?.GetValue<string>() == exposure.Protocol &&
                    publisher["URL"]?.GetValue<string>() == exposure.HostIp).ToArray();
                if (publishers.Length != 1 || publishers[0]["PublishedPort"]?.GetValue<int>() is not (>= 1 and <= 65535) ||
                    exposure.Published is { } requested && publishers[0]["PublishedPort"]!.GetValue<int>() != requested)
                {
                    throw new InvalidOperationException($"Docker Compose service '{expected.Resource}' has an unexpected published binding.");
                }
                var published = publishers[0]["PublishedPort"]!.GetValue<int>();
                var host = exposure.HostIp is "0.0.0.0" or "::" ? "localhost" : exposure.HostIp.Contains(':') ? $"[{exposure.HostIp}]" : exposure.HostIp;
                endpoints.Add(new JsonObject
                {
                    ["name"] = exposure.Endpoint, ["hostIp"] = exposure.HostIp, ["port"] = published, ["url"] = $"{exposure.Scheme}://{host}:{published}"
                });
            }
            services.Add(new JsonObject
            {
                ["resource"] = expected.Resource, ["service"] = expected.Service, ["state"] = "running",
                ["health"] = row["Health"]?.GetValue<string>() == "healthy" ? "healthy" : "not-configured", ["endpoints"] = endpoints
            });
        }

        return new() { ["schemaVersion"] = 1, ["projectName"] = Text(plan.Compose, "name"), ["services"] = services };
    }
}
