// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Aspire.Hosting.Native.Authoring;

namespace NativeIntegrations.DockerCompose;

/// <summary>Pure projection of the native publishing contract, matching docker-compose.mts.</summary>
public static class DockerComposeGenerator
{
    public static string ProjectName(string applicationDirectory, string environmentName, string name)
    {
        var directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(applicationDirectory));
        ComposeJson.Text(environmentName);
        ComposeJson.Text(name);
        var identity = Hash(ComposeJson.Json(new JsonArray(directory, environmentName, name)));

        return $"aspire-{Slug(Path.GetFileName(directory))[..Math.Min(Slug(Path.GetFileName(directory)).Length, 16)]}-{ShortSlug(name, 12)}-{ShortSlug(environmentName, 12)}-{identity}";
    }

    public static string ParameterVariable(string name) =>
        "ASPIRE_PARAMETER_" + Regex.Replace(ComposeJson.Text(name).ToUpperInvariant(), "[^A-Z0-9_]", "_", RegexOptions.CultureInvariant);

    public static DockerComposePlan Generate(PublishingContext context, string name) => Generate(context, name, new HashSet<string>(StringComparer.Ordinal));

    internal static DockerComposePlan Generate(PublishingContext context, string name, IReadOnlySet<string> customizable)
    {
        if (context.Model["schemaVersion"]?.GetValue<int>() != 1)
        {
            throw new InvalidOperationException("Unsupported deployment model schema.");
        }
        var project = ProjectName(context.ApplicationDirectory, context.EnvironmentName, name);
        var all = Objects(context.Model["resources"]).ToArray();
        var environments = all.Where(resource => Text(resource, "type") == "docker-compose/environment").ToArray();
        var environment = environments.SingleOrDefault(resource => Text(resource, "name") == name);
        if (environments.Length > 0 && environment is null)
        {
            throw new InvalidOperationException($"Docker Compose environment '{name}' is not present in the native graph.");
        }
        var resources = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        var options = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        var allNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var resource in all.OrderBy(resource => Text(resource, "name"), StringComparer.Ordinal))
        {
            var resourceName = ComposeJson.Text(Text(resource, "name"));
            if (!allNames.Add(resourceName))
            {
                throw new InvalidOperationException($"Duplicate resource '{resourceName}'.");
            }
            if (environments.Contains(resource) || Text(resource, "type") == "hosting/parameter")
            {
                continue;
            }
            var configuration = Options(resource);
            if (Boolean(configuration, "exclude"))
            {
                continue;
            }
            var metadata = resource["configuration"]?["publish.computeEnvironment"]?.GetValue<string>();
            var assignment = resource.ContainsKey("computeEnvironment") ? resource["computeEnvironment"]?.GetValue<string>() : metadata;
            if (resource.ContainsKey("computeEnvironment") && metadata is not null && metadata != assignment)
            {
                throw new InvalidOperationException($"Resource '{resourceName}' has conflicting compute-environment metadata.");
            }
            if (assignment is not null && !environments.Any(value => Text(value, "name") == assignment))
            {
                throw new InvalidOperationException($"Resource '{resourceName}' references unknown compute environment '{assignment}'.");
            }
            if (assignment is null && environments.Length > 1)
            {
                throw new InvalidOperationException($"Resource '{resourceName}' must explicitly select a compute environment.");
            }
            if (assignment is not null && assignment != name)
            {
                continue;
            }
            if (Text(resource, "kind") is not ("container" or "executable"))
            {
                throw new InvalidOperationException($"Resource '{resourceName}' is opaque; explicitly exclude it from Docker Compose.");
            }
            var service = ServiceName(resourceName);
            if (names.ContainsValue(service))
            {
                throw new InvalidOperationException("Resource names collide after Compose normalization.");
            }
            resources.Add(resourceName, resource);
            names.Add(resourceName, service);
            options.Add(resourceName, configuration);
        }
        ValidateDependencies(resources);
        var parameters = Strings(context.Model["parameters"]).Order(StringComparer.Ordinal).ToDictionary(value => value, ParameterVariable, StringComparer.Ordinal);
        if (parameters.Values.Distinct(StringComparer.Ordinal).Count() != parameters.Count)
        {
            throw new InvalidOperationException("Parameter names collide after environment-variable normalization.");
        }
        var inputs = new JsonObject
        {
            ["schemaVersion"] = 1, ["projectName"] = project, ["parameters"] = new JsonArray(), ["variables"] = new JsonObject()
        };
        var used = new SortedSet<string>(StringComparer.Ordinal);
        var variables = new Dictionary<string, string>(StringComparer.Ordinal);
        var compose = new JsonObject { ["name"] = project, ["services"] = new JsonObject() };
        var plan = new DockerComposePlan(compose, inputs, []);
        var volumes = new SortedDictionary<string, JsonNode?>(StringComparer.Ordinal);
        var volumeOwners = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (resourceName, resource) in resources)
        {
            var configuration = options[resourceName];
            var image = configuration["image"]?.GetValue<string>() ?? resource["image"]?.GetValue<string>();
            var deferred = customizable.Contains(resourceName);
            if ((!HasText(image) || Text(resource, "kind") == "executable" &&
                (configuration["image"] is null || configuration["build"] is null)) && !deferred)
            {
                throw new InvalidOperationException($"Resource '{resourceName}' requires a deployment image and explicit executable build metadata.");
            }
            if (image is not null)
            {
                ComposeJson.ValidateImage(image);
            }
            var lifecycle = Strings(resource["lifecycle"]).Any();
            if ((lifecycle && (!Boolean(configuration, "replaceRunLifecycle") || configuration["healthcheck"] is null) ||
                Boolean(configuration, "replaceRunLifecycle") && configuration["healthcheck"] is null) && !deferred)
            {
                throw new InvalidOperationException($"Resource '{resourceName}' must explicitly replace host lifecycle with a target healthcheck or be excluded.");
            }
            var service = new JsonObject
            {
                ["image"] = ComposeJson.Literal(image ?? ""), ["labels"] = new JsonObject { ["aspire.resource"] = ComposeJson.Literal(resourceName) }
            };
            if (configuration["build"] is JsonObject build)
            {
                service["build"] = ComposeJson.BuildPaths(new(Text(build, "context"), Text(build, "dockerfile")), context.ApplicationDirectory);
            }
            if (Text(resource, "kind") == "container" && resource["entrypoint"] is not null)
            {
                service["entrypoint"] = new JsonArray(ComposeJson.Literal(ComposeJson.Text(Text(resource, "entrypoint"))));
            }
            var arguments = Objects(resource["arguments"]).ToArray();
            if (arguments.Length > 0)
            {
                service["command"] = ComposeJson.Strings(arguments.Select(value => Render(Normalize(value))));
            }
            var serviceEnvironment = new SortedDictionary<string, JsonNode?>(StringComparer.Ordinal);
            foreach (var (key, value) in resource["environment"]!.AsObject())
            {
                ValidateEnvironmentName(key);
                serviceEnvironment.Add(key, JsonValue.Create(Render(Normalize(value!.AsObject()))));
            }
            var endpoints = Objects(resource["endpoints"]).OrderBy(value => Text(value, "name"), StringComparer.Ordinal).ToArray();
            if (endpoints.Select(endpoint => Text(endpoint, "name")).Distinct(StringComparer.Ordinal).Count() != endpoints.Length)
            {
                throw new InvalidOperationException($"Resource '{resourceName}' has duplicate endpoints.");
            }
            foreach (var endpoint in endpoints)
            {
                ComposeJson.Port(Integer(endpoint, "targetPort"));
                if (Text(endpoint, "protocol") is not ("tcp" or "udp") ||
                    !Regex.IsMatch(Text(endpoint, "scheme"), "^[a-z][a-z0-9+.-]*$", RegexOptions.CultureInvariant))
                {
                    throw new InvalidOperationException($"Resource '{resourceName}' has unsupported endpoint protocol or scheme.");
                }
                if (endpoint["environmentVariable"]?.GetValue<string>() is { Length: > 0 } variable)
                {
                    ValidateEnvironmentName(variable);
                    var target = Integer(endpoint, "targetPort").ToString(CultureInfo.InvariantCulture);
                    if (serviceEnvironment.TryGetValue(variable, out var existing) && existing!.GetValue<string>() != target)
                    {
                        throw new InvalidOperationException($"Endpoint '{resourceName}/{Text(endpoint, "name")}' port environment conflicts with authored environment.");
                    }
                    serviceEnvironment[variable] = JsonValue.Create(target);
                }
            }
            if (serviceEnvironment.Count > 0)
            {
                service["environment"] = new JsonObject(serviceEnvironment);
            }
            if (endpoints.Length > 0)
            {
                service["expose"] = ComposeJson.Strings(endpoints.Select(endpoint =>
                    $"{Integer(endpoint, "targetPort")}/{Text(endpoint, "protocol")}").Distinct().Order(StringComparer.Ordinal));
            }
            var exposed = new List<ComposePublishedPort>();
            var exposures = configuration["ports"] is not null ? Objects(configuration["ports"]) :
                endpoints.Where(endpoint => Boolean(endpoint, "isExternal")).Select(endpoint =>
                {
                    var exposure = new JsonObject { ["endpoint"] = Text(endpoint, "name") };
                    if (endpoint["hostPort"]?.GetValue<int>() is > 0 and var hostPort)
                    {
                        exposure["published"] = hostPort;
                    }

                    return exposure;
                });
            foreach (var exposure in exposures.OrderBy(value => Text(value, "endpoint"), StringComparer.Ordinal))
            {
                var endpoint = endpoints.SingleOrDefault(endpoint => Text(endpoint, "name") == Text(exposure, "endpoint"))
                    ?? throw new InvalidOperationException($"Exposed endpoint '{resourceName}/{Text(exposure, "endpoint")}' does not exist.");
                exposed.Add(new(Text(endpoint, "name"), Integer(endpoint, "targetPort"), Text(endpoint, "protocol"), Text(endpoint, "scheme"),
                    exposure["hostIp"]?.GetValue<string>() ?? "127.0.0.1", exposure["published"]?.GetValue<int>()));
            }
            if (exposed.Count > 0)
            {
                service["ports"] = new JsonArray(exposed.Select(exposure =>
                {
                    var value = new JsonObject { ["target"] = exposure.Target };
                    if (exposure.Published is { } published)
                    {
                        value["published"] = published.ToString(CultureInfo.InvariantCulture);
                    }
                    value["host_ip"] = exposure.HostIp;
                    value["protocol"] = exposure.Protocol;

                    return (JsonNode)value;
                }).ToArray());
            }
            var mounts = Objects(resource["mounts"]).OrderBy(mount => Text(mount, "target") + "\0" + Text(mount, "source"), StringComparer.Ordinal).ToArray();
            if (mounts.Length > 0)
            {
                service["volumes"] = new JsonArray(mounts.Select((mount, index) =>
                {
                    var source = ComposeJson.Text(Text(mount, "source"));
                    var target = Text(mount, "target");
                    ComposeJson.AbsoluteTarget(target);
                    var kind = Text(mount, "kind");
                    if (kind == "bind")
                    {
                        var variable = Regex.Replace(resourceName.ToUpperInvariant(), "[^A-Z0-9_]", "_", RegexOptions.CultureInvariant) + "_BINDMOUNT_" + index;
                        var captured = (inputs["environment"] ??= new JsonObject()).AsObject();
                        if (captured.ContainsKey(variable))
                        {
                            throw new InvalidOperationException("Bind mount environment-variable names collide after normalization.");
                        }
                        captured[variable] = new JsonObject
                        {
                            ["description"] = $"Bind mount source for {resourceName}:{target}", ["defaultValue"] = source, ["secret"] = false
                        };
                        source = Placeholder(variable);
                    }
                    else if (kind == "volume")
                    {
                        ComposeJson.Key(source);
                        var key = ServiceName(source);
                        if (volumeOwners.TryGetValue(key, out var owner) && owner != source)
                        {
                            throw new InvalidOperationException("Volume names collide after Compose normalization.");
                        }
                        volumeOwners[key] = source;
                        volumes[key] = new JsonObject();
                        source = key;
                    }
                    else
                    {
                        throw new InvalidOperationException($"Unsupported mount kind '{kind}'.");
                    }

                    return (JsonNode)new JsonObject
                    {
                        ["type"] = kind, ["source"] = source, ["target"] = ComposeJson.Literal(target), ["read_only"] = Boolean(mount, "readOnly")
                    };
                }).ToArray());
            }
            var dependencies = Strings(resource["dependencies"]).Distinct().Order(StringComparer.Ordinal).ToArray();
            if (dependencies.Length > 0)
            {
                service["depends_on"] = new JsonObject(dependencies.Select(dependency =>
                    KeyValuePair.Create(names[dependency], (JsonNode?)new JsonObject
                    {
                        ["condition"] = options[dependency]["healthcheck"] is null ? "service_started" : "service_healthy"
                    })));
            }
            if (configuration["healthcheck"] is JsonObject healthcheck)
            {
                service["healthcheck"] = ComposeJson.Healthcheck(ComposeJson.ReadHealthcheck(healthcheck), escape: true);
            }
            compose["services"]!.AsObject().Add(names[resourceName], service);
            plan.Services.Add(new(resourceName, names[resourceName], configuration["healthcheck"] is not null, exposed));
        }
        if (volumes.Count > 0)
        {
            compose["volumes"] = new JsonObject(volumes);
        }
        inputs["parameters"] = new JsonArray(used.Select(parameter => (JsonNode)new JsonObject
        {
            ["name"] = parameter, ["environmentVariable"] = parameters[parameter]
        }).ToArray());
        if (context.Model.ContainsKey("secretParameters") || context.Model.ContainsKey("parameterDefaults"))
        {
            CaptureParameters(plan, context.Model);
        }
        if (environment is not null)
        {
            ConfigureEnvironment(plan, environment);
        }
        if (compose["services"]!.AsObject().Count == 0)
        {
            throw new InvalidOperationException("Docker Compose requires at least one service.");
        }

        return plan;

        JsonObject Normalize(JsonObject value, int depth = 0)
        {
            if (depth > 64)
            {
                throw new InvalidOperationException("Excessively nested publish expression.");
            }
            switch (Text(value, "kind"))
            {
                case "literal":
                    return new() { ["kind"] = "literal", ["value"] = ComposeJson.Text(Text(value, "value"), true) };
                case "parameter":
                    var parameter = Text(value, "name");
                    if (!parameters.ContainsKey(parameter))
                    {
                        throw new InvalidOperationException($"Expression references undeclared parameter '{parameter}'.");
                    }
                    used.Add(parameter);

                    return new() { ["kind"] = "parameter", ["name"] = parameter };
                case "endpoint":
                    EndpointValue(value, resources, names);

                    return new() { ["kind"] = "endpoint", ["resource"] = Text(value, "resource"), ["endpoint"] = Text(value, "endpoint"), ["property"] = Text(value, "property") };
                case "composite":
                    return new() { ["kind"] = "composite", ["parts"] = new JsonArray(Objects(value["parts"]).Select(part => (JsonNode)Normalize(part, depth + 1)).ToArray()) };
                case "uriEncoded":
                    return new() { ["kind"] = "uriEncoded", ["value"] = Normalize(value["value"]!.AsObject(), depth + 1) };
                default:
                    throw new InvalidOperationException("Unsupported publish expression.");
            }
        }

        string Render(JsonObject value)
        {
            switch (Text(value, "kind"))
            {
                case "literal":
                    return ComposeJson.Literal(Text(value, "value"));
                case "endpoint":
                    return ComposeJson.Literal(EndpointValue(value, resources, names));
                case "parameter":
                    return Placeholder(parameters[Text(value, "name")]);
                case "composite":
                    return string.Concat(Objects(value["parts"]).Select(Render));
                case "uriEncoded":
                    if (!ContainsParameter(value))
                    {
                        return ComposeJson.Literal(Evaluate(value, resources, names, new Dictionary<string, string>()));
                    }
                    var key = value.ToJsonString(ComposeJson.s_indented);
                    if (!variables.TryGetValue(key, out var variable))
                    {
                        variable = $"ASPIRE_COMPOSE_EXPR_{variables.Count + 1:D4}";
                        variables.Add(key, variable);
                        inputs["variables"]!.AsObject()[variable] = value.DeepClone();
                    }

                    return Placeholder(variable);
                default:
                    throw new InvalidOperationException("Unsupported publish expression.");
            }
        }
    }

    public static IReadOnlyDictionary<string, string> ResolveInputs(DockerComposePlan plan, JsonObject model,
        IReadOnlyDictionary<string, string?> environment)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var parameter in Objects(plan.Inputs["parameters"]))
        {
            var name = Text(parameter, "name");
            var variable = Text(parameter, "environmentVariable");
            var secret = model["secretParameters"] is null || Strings(model["secretParameters"]).Contains(name, StringComparer.Ordinal);
            environment.TryGetValue(variable, out var value);
            value ??= secret ? null : plan.Inputs["environment"]?[variable]?["defaultValue"]?.GetValue<string>() ??
                model["parameterDefaults"]?[name]?.GetValue<string>();
            result[variable] = RequiredInput(variable, value);
        }
        foreach (var (variable, node) in plan.Inputs["environment"]?.AsObject() ?? [])
        {
            var entry = node!.AsObject();
            environment.TryGetValue(variable, out var value);
            value ??= Boolean(entry, "secret") ? null : entry["defaultValue"]?.GetValue<string>();
            result[variable] = RequiredInput(variable, value);
        }
        var resources = Objects(model["resources"]).ToDictionary(resource => Text(resource, "name"), StringComparer.Ordinal);
        var names = plan.Services.ToDictionary(service => service.Resource, service => service.Service, StringComparer.Ordinal);
        foreach (var (variable, node) in plan.Inputs["variables"]!.AsObject())
        {
            result[variable] = Evaluate(node!.AsObject(), resources, names, result);
        }

        return result;
    }

    internal static void CaptureParameters(DockerComposePlan plan, JsonObject model)
    {
        foreach (var parameter in Objects(plan.Inputs["parameters"]))
        {
            var name = Text(parameter, "name");
            var variable = Text(parameter, "environmentVariable");
            var captured = (plan.Inputs["environment"] ??= new JsonObject()).AsObject();
            if (captured.ContainsKey(variable))
            {
                continue;
            }
            var secret = model["secretParameters"] is null || Strings(model["secretParameters"]).Contains(name, StringComparer.Ordinal);
            var entry = new JsonObject { ["description"] = $"Deployment parameter {name}", ["secret"] = secret };
            if (!secret && model["parameterDefaults"]?[name] is { } defaultValue)
            {
                entry["defaultValue"] = defaultValue.DeepClone();
            }
            captured[variable] = entry;
        }
    }

    private static void ConfigureEnvironment(DockerComposePlan plan, JsonObject environment)
    {
        var source = environment["configuration"]?["publish.dockerComposeEnvironment"]?.GetValue<string>();
        var configuration = source is null ? new JsonObject
        {
            ["defaultNetworkName"] = null, ["dashboardEnabled"] = true, ["dashboardHostPort"] = null, ["dashboardForwardedHeaders"] = false
        } : JsonNode.Parse(source) as JsonObject ?? throw new ArgumentException("Docker Compose environment configuration must be an object.");
        Fields(configuration, ["defaultNetworkName", "dashboardEnabled", "dashboardHostPort", "dashboardForwardedHeaders"]);
        foreach (var property in new[] { "defaultNetworkName", "dashboardEnabled", "dashboardHostPort", "dashboardForwardedHeaders" })
        {
            if (!configuration.ContainsKey(property))
            {
                throw new ArgumentException($"Docker Compose environment configuration requires '{property}'.");
            }
        }
        foreach (var property in new[] { "dashboardEnabled", "dashboardForwardedHeaders" })
        {
            if (configuration[property] is not JsonValue value || !value.TryGetValue<bool>(out _))
            {
                throw new ArgumentException($"Docker Compose environment '{property}' must be a boolean.");
            }
        }
        if (configuration["dashboardHostPort"] is { } configuredPort)
        {
            _ = configuredPort.GetValue<int>();
        }
        if (configuration["defaultNetworkName"] is { } network)
        {
            plan.Compose["networks"] = new JsonObject { ["default"] = new JsonObject
            {
                ["name"] = ComposeJson.Literal(ComposeJson.Text(network.GetValue<string>())), ["external"] = true
            } };
        }
        if (!Boolean(configuration, "dashboardEnabled"))
        {
            return;
        }
        var resourceName = Text(environment, "name") + "-dashboard";
        var serviceName = ServiceName(resourceName);
        var services = plan.Compose["services"]!.AsObject();
        if (services.ContainsKey(serviceName))
        {
            throw new InvalidOperationException($"Dashboard service '{serviceName}' collides with an authored workload.");
        }
        var hostPort = configuration["dashboardHostPort"]?.GetValue<int>();
        if (hostPort is { } port)
        {
            ComposeJson.Port(port);
        }
        var variables = new JsonObject
        {
            ["ASPNETCORE_URLS"] = "http://0.0.0.0:18888", ["DOTNET_DASHBOARD_OTLP_ENDPOINT_URL"] = "http://0.0.0.0:18889",
            ["Dashboard__Frontend__AuthMode"] = "BrowserToken",
            ["Dashboard__Frontend__BrowserToken"] = Secret(ParameterVariable(Text(environment, "name") + "-dashboard-token"), "Dashboard browser token"),
            ["Dashboard__Otlp__AuthMode"] = "ApiKey",
            ["Dashboard__Otlp__PrimaryApiKey"] = Secret(ParameterVariable(Text(environment, "name") + "-dashboard-otlp-key"), "Dashboard OTLP API key"),
            ["ASPIRE_DASHBOARD_TELEMETRY_OPTOUT"] = "true",
            ["ASPIRE_DASHBOARD_FORWARDEDHEADERS_ENABLED"] = Boolean(configuration, "dashboardForwardedHeaders") ? "true" : "false"
        };
        var dashboard = new JsonObject
        {
            ["image"] = "mcr.microsoft.com/dotnet/aspire-dashboard:13.0", ["restart"] = "always",
            ["labels"] = new JsonObject { ["aspire.resource"] = resourceName }, ["environment"] = variables,
            ["expose"] = new JsonArray("18888/tcp", "18889/tcp")
        };
        if (hostPort is { } published)
        {
            dashboard["ports"] = new JsonArray(new JsonObject
            {
                ["target"] = 18888, ["published"] = published.ToString(CultureInfo.InvariantCulture), ["host_ip"] = "127.0.0.1", ["protocol"] = "tcp"
            });
        }
        services[serviceName] = dashboard;
        plan.Services.Add(new(resourceName, serviceName, false, hostPort is { } bound ?
            [new("http", 18888, "tcp", "http", "127.0.0.1", bound)] : []));

        string Secret(string variable, string description)
        {
            var captured = (plan.Inputs["environment"] ??= new JsonObject()).AsObject();
            if (captured.ContainsKey(variable) || Objects(plan.Inputs["parameters"]).Any(parameter => Text(parameter, "environmentVariable") == variable))
            {
                throw new InvalidOperationException($"Dashboard deployment input '{variable}' collides with a declared parameter.");
            }
            captured[variable] = new JsonObject { ["description"] = description, ["secret"] = true };

            return Placeholder(variable);
        }
    }

    internal static JsonObject Options(JsonObject resource)
    {
        var options = resource["configuration"]?["publish.dockerCompose"] is { } source ? JsonNode.Parse(source.GetValue<string>())!.AsObject() : new JsonObject();
        ValidateOptions(options);

        return options;
    }
    internal static void ValidateOptions(JsonObject options)
    {
        Fields(options, ["build", "image", "healthcheck", "ports", "exclude", "replaceRunLifecycle"]);
        foreach (var flag in new[] { "exclude", "replaceRunLifecycle" })
        {
            if (options.ContainsKey(flag))
            {
                _ = options[flag]!.GetValue<bool>();
            }
        }
        if (options.ContainsKey("image"))
        {
            ComposeJson.ValidateImage(Text(options, "image"));
        }
        if (options.ContainsKey("build"))
        {
            var build = options["build"]?.AsObject() ?? throw new ArgumentException("Build configuration must be an object.", nameof(options));
            Fields(build, ["context", "dockerfile"]);
            ComposeJson.Text(Text(build, "context"));
            ComposeJson.Text(Text(build, "dockerfile"));
        }
        if (options.ContainsKey("healthcheck"))
        {
            var health = options["healthcheck"]?.AsObject() ?? throw new ArgumentException("Healthcheck must be an object.", nameof(options));
            Fields(health, ["test", "interval", "timeout", "retries"]);
            ComposeJson.Healthcheck(ComposeJson.ReadHealthcheck(health));
        }
        var endpoints = new HashSet<string>(StringComparer.Ordinal);
        if (options.ContainsKey("ports"))
        {
            foreach (var exposure in Objects(options["ports"]))
            {
                Fields(exposure, ["endpoint", "published", "hostIp"]);
                if (!endpoints.Add(ComposeJson.Text(Text(exposure, "endpoint"))))
                {
                    throw new ArgumentException("An endpoint can be exposed only once.", nameof(options));
                }
                if (exposure.ContainsKey("published"))
                {
                    ComposeJson.Port(Integer(exposure, "published"));
                }
                if (exposure.ContainsKey("hostIp") && !IPAddress.TryParse(Text(exposure, "hostIp"), out _))
                {
                    throw new ArgumentException("Host IP must be an IPv4 or IPv6 address.", nameof(options));
                }
            }
        }
    }
    internal static string ServiceName(string value) => Regex.IsMatch(value, "^[a-z0-9][a-z0-9-]{0,62}$", RegexOptions.CultureInvariant) ? value : $"{Slug(value)}-{Hash(value)}";
    internal static string Placeholder(string variable) => "${" + variable + ":?Required deployment input " + variable + "}";
    internal static IEnumerable<JsonObject> Objects(JsonNode? value) => (value ?? throw new ArgumentException("Expected a JSON array.")).AsArray().Select(node => node!.AsObject());
    internal static IEnumerable<string> Strings(JsonNode? value) => (value ?? throw new ArgumentException("Expected a JSON array.")).AsArray().Select(node => node!.GetValue<string>());
    internal static string Text(JsonObject value, string property) => value[property]?.GetValue<string>() ?? throw new ArgumentException($"Missing string '{property}'.");
    internal static bool Boolean(JsonObject value, string property) => value[property]?.GetValue<bool>() ?? false;
    private static int Integer(JsonObject value, string property) => value[property]!.GetValue<int>();
    private static bool HasText(string? value) => !string.IsNullOrWhiteSpace(value);
    private static string ShortSlug(string value, int length) => Slug(value)[..Math.Min(Slug(value).Length, length)];
    private static string Slug(string value)
    {
        var slug = Regex.Replace(value.ToLowerInvariant(), "[^a-z0-9-]+", "-", RegexOptions.CultureInvariant).Trim('-');

        return slug.Length == 0 ? "resource" : slug[..Math.Min(slug.Length, 35)];
    }
    internal static string Hash(string value)
    {
        // Must match the TypeScript integration's FNV-1a/64 identity exactly so both
        // implementations address the same project, service and retained volume names.
        var hash = 14695981039346656037UL;
        foreach (var item in Encoding.UTF8.GetBytes(value))
        {
            hash = unchecked((hash ^ item) * 1099511628211UL);
        }

        return hash.ToString("x16", CultureInfo.InvariantCulture);
    }
    private static void Fields(JsonObject value, string[] allowed)
    {
        if (value.Any(pair => !allowed.Contains(pair.Key, StringComparer.Ordinal)))
        {
            throw new ArgumentException("Unsupported Compose configuration field.", nameof(value));
        }
    }
    private static void ValidateEnvironmentName(string name)
    {
        if (!Regex.IsMatch(name, "^[a-zA-Z_][a-zA-Z0-9_]*$", RegexOptions.CultureInvariant))
        {
            throw new ArgumentException("Invalid environment variable name.", nameof(name));
        }
    }
    private static bool ContainsParameter(JsonObject value) => Text(value, "kind") switch
    {
        "parameter" => true,
        "composite" => Objects(value["parts"]).Any(ContainsParameter),
        "uriEncoded" => ContainsParameter(value["value"]!.AsObject()),
        _ => false
    };
    private static string Evaluate(JsonObject value, IReadOnlyDictionary<string, JsonObject> resources,
        IReadOnlyDictionary<string, string> names, IReadOnlyDictionary<string, string> parameters) => Text(value, "kind") switch
    {
        "literal" => Text(value, "value"),
        "parameter" => parameters[ParameterVariable(Text(value, "name"))],
        "endpoint" => EndpointValue(value, resources, names),
        "composite" => string.Concat(Objects(value["parts"]).Select(part => Evaluate(part, resources, names, parameters))),
        "uriEncoded" => EncodeUri(Evaluate(value["value"]!.AsObject(), resources, names, parameters)),
        _ => throw new InvalidOperationException("Unsupported publish expression.")
    };
    internal static string EncodeUri(string value) =>
        Uri.EscapeDataString(value).Replace("%21", "!", StringComparison.Ordinal).Replace("%27", "'", StringComparison.Ordinal)
            .Replace("%28", "(", StringComparison.Ordinal).Replace("%29", ")", StringComparison.Ordinal).Replace("%2A", "*", StringComparison.Ordinal);
    private static string RequiredInput(string variable, string? value) =>
        string.IsNullOrEmpty(value) || value.Contains('\0') ? throw new InvalidOperationException($"Set required deployment input {variable} explicitly.") : value;
    private static string EndpointValue(JsonObject value, IReadOnlyDictionary<string, JsonObject> resources, IReadOnlyDictionary<string, string> names)
    {
        var name = Text(value, "resource");
        if (!resources.TryGetValue(name, out var resource) || !names.TryGetValue(name, out var host))
        {
            throw new InvalidOperationException($"Endpoint reference targets missing or excluded service '{name}'; references across compute environments are not supported.");
        }
        var endpoint = Objects(resource["endpoints"]).SingleOrDefault(endpoint => Text(endpoint, "name") == Text(value, "endpoint"))
            ?? throw new InvalidOperationException($"Endpoint '{Text(value, "endpoint")}' is missing on '{name}'.");
        var port = Integer(endpoint, "targetPort").ToString(CultureInfo.InvariantCulture);

        return Text(value, "property") switch
        {
            "Host" => host,
            "Port" => port,
            "HostAndPort" => $"{host}:{port}",
            "Scheme" => Text(endpoint, "scheme"),
            "Url" => $"{Text(endpoint, "scheme")}://{host}:{port}",
            _ => throw new InvalidOperationException("Unsupported endpoint property.")
        };
    }
    private static void ValidateDependencies(IReadOnlyDictionary<string, JsonObject> resources)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var active = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in resources.Keys)
        {
            Visit(name);
        }

        void Visit(string name)
        {
            if (visited.Contains(name))
            {
                return;
            }
            if (!resources.TryGetValue(name, out var resource))
            {
                throw new InvalidOperationException($"Wait dependency targets missing or excluded service '{name}'; dependencies across compute environments are not supported.");
            }
            if (!active.Add(name))
            {
                throw new InvalidOperationException($"Compose wait dependency cycle includes '{name}'.");
            }
            foreach (var dependency in Strings(resource["dependencies"]))
            {
                Visit(dependency);
            }
            active.Remove(name);
            visited.Add(name);
        }
    }
}
