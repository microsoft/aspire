// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Text;
using System.Text.Json;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Dcp.Process;
using Aspire.Hosting.Docker;

namespace Aspire.Hosting;

/// <summary>
/// Imports a bounded Docker Compose development stack into native Aspire resources.
/// </summary>
public static class DockerComposeImportExtensions
{
    /// <summary>
    /// Normalizes a Compose file with Docker Compose and imports its services.
    /// </summary>
    /// <param name="builder">The application builder.</param>
    /// <param name="name">The prefix for imported resource names.</param>
    /// <param name="path">The Compose file, relative to the AppHost directory.</param>
    /// <param name="configure">Optional service replacements.</param>
    /// <param name="cancellationToken">Cancels Compose normalization.</param>
    /// <returns>The imported services.</returns>
    /// <remarks>
    /// This run-mode proof of concept requires Docker Compose v2 or later. Unsupported
    /// configuration throws before any imported resources are added. Whole absolute URLs
    /// in environment variables referring to Compose services become Aspire endpoint references.
    /// Other environment values remain literal. Only import trusted files: interpolation
    /// and env_file loading run with the AppHost's environment and filesystem access.
    /// <code>
    /// var compose = await builder.AddDockerComposeFileAsync("legacy", "../compose.yaml");
    /// var gateway = compose.GetService("gateway");
    /// </code>
    /// </remarks>
    [AspireExportIgnore(Reason = "Compose import is a C#-only proof of concept; polyglot API design is deferred.")]
    public static async Task<DockerComposeImport> AddDockerComposeFileAsync(
        this IDistributedApplicationBuilder builder,
        [ResourceName] string name,
        string path,
        Action<DockerComposeImportOptions>? configure = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!builder.ExecutionContext.IsRunMode)
        {
            throw new NotSupportedException("Compose import currently supports run mode only, not publishing.");
        }
        cancellationToken.ThrowIfCancellationRequested();

        var file = Path.GetFullPath(path, builder.AppHostDirectory);
        if (!File.Exists(file))
        {
            throw new FileNotFoundException("Compose file was not found.", file);
        }

        var options = new DockerComposeImportOptions();
        configure?.Invoke(options);
        var output = new StringBuilder();
        var errors = new StringBuilder();
        var (execution, process) = ProcessUtil.Run(new ProcessSpec("docker")
        {
            ResolveExecutablePath = true,
            WorkingDirectory = Path.GetDirectoryName(file),
            ArgumentList = ["compose", "--file", file, "config", "--format", "json"],
            OnOutputData = line => output.AppendLine(line),
            OnErrorData = line => errors.AppendLine(line),
            ThrowOnNonZeroReturnCode = false,
            RetainedOutputLineCount = 0
        });
        await using (process.ConfigureAwait(false))
        {
            var result = await execution.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
            if (result.ExitCode != 0)
            {
                throw new InvalidOperationException($"Docker Compose configuration failed (exit {result.ExitCode}). Ensure Docker Compose v2 or later is installed. {errors}");
            }
            // Compose warns about unset interpolation variables but substitutes empty strings.
            // Do not silently import a different stack when this changes connectivity or credentials.
            if (errors.Length > 0)
            {
                throw new InvalidOperationException($"Docker Compose emitted configuration diagnostics; resolve them before importing: {errors}");
            }
        }

        return ImportNormalized(builder, name, output.ToString(), options);
    }

    internal static DockerComposeImport ImportNormalized(
        IDistributedApplicationBuilder builder, string name, string json, DockerComposeImportOptions options)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        ValidateKeys(root, "$", "name", "services", "networks");
        if (root.TryGetProperty("networks", out var networks))
        {
            ValidateKeys(networks, "networks", "default");
            if (networks.TryGetProperty("default", out var network))
            {
                ValidateKeys(network, "networks.default", "name", "ipam");
                if (network.TryGetProperty("ipam", out var ipam))
                {
                    ValidateKeys(ipam, "networks.default.ipam");
                }
            }
        }

        var definitions = root.GetProperty("services").EnumerateObject()
            .ToDictionary(p => p.Name, p => ParseService(p.Name, p.Value), StringComparer.Ordinal);
        if (definitions.Count == 0)
        {
            throw Unsupported("services", "at least one service is required");
        }
        var replacementIdentities = new HashSet<IResourceWithEndpoints>(ReferenceEqualityComparer.Instance);
        foreach (var (service, replacement) in options.Replacements)
        {
            if (!replacementIdentities.Add(replacement))
            {
                throw Unsupported($"services.{service}", "one resource cannot replace multiple Compose services");
            }
            if (!definitions.TryGetValue(service, out var definition))
            {
                throw Unsupported($"services.{service}", "replacement service does not exist");
            }
            if (!builder.Resources.Contains(replacement))
            {
                throw Unsupported($"services.{service}", "replacement must belong to this AppHost");
            }
            foreach (var port in definition.Ports)
            {
                if (!new EndpointReference(replacement, EndpointName(port.Target)).Exists)
                {
                    throw Unsupported($"services.{service}", $"replacement requires endpoint '{EndpointName(port.Target)}'");
                }
            }
        }
        foreach (var (service, definition) in definitions)
        {
            if (service.Any(char.IsAsciiLetterUpper))
            {
                throw Unsupported($"services.{service}", "service names must be lowercase for unambiguous URL host matching");
            }
            if (!ModelName.TryValidateName("Compose service", service, out var serviceNameError))
            {
                throw Unsupported($"services.{service}", serviceNameError!);
            }
            if (!options.Replacements.ContainsKey(service) &&
                !ModelName.TryValidateName("Resource", $"{name}-{service}", out var nameError))
            {
                throw Unsupported($"services.{service}", nameError!);
            }
            if (!options.Replacements.ContainsKey(service) &&
                builder.Resources.Any(r => string.Equals(r.Name, $"{name}-{service}", StringComparison.OrdinalIgnoreCase)))
            {
                throw Unsupported($"services.{service}", $"resource '{name}-{service}' already exists");
            }
            if (!options.Replacements.ContainsKey(service) &&
                builder.Resources.OfType<ContainerResource>().Any(r =>
                    string.Equals(r.Name, service, StringComparison.OrdinalIgnoreCase) ||
                    r.Annotations.OfType<ContainerNetworkAliasAnnotation>().Any(a =>
                        string.Equals(a.Alias, service, StringComparison.OrdinalIgnoreCase))))
            {
                throw Unsupported($"services.{service}", $"network alias '{service}' is already used by another resource");
            }
            if (definitions.Keys.Any(other => other != service && !options.Replacements.ContainsKey(other) &&
                string.Equals($"{name}-{other}", service, StringComparison.OrdinalIgnoreCase)))
            {
                throw Unsupported($"services.{service}", "service alias conflicts with another imported resource name");
            }
            foreach (var dependency in definition.Dependencies)
            {
                if (!definitions.ContainsKey(dependency) || dependency == service)
                {
                    throw Unsupported($"services.{service}.depends_on.{dependency}", "dependency must be another imported service");
                }
            }
            foreach (var (key, value) in definition.Environment)
            {
                if (TryServiceUri(value, definitions, out var uri))
                {
                    var target = definitions[uri.Host];
                    if (uri.UserInfo.Length > 0 || !target.Ports.Any(p => p.Target == uri.Port))
                    {
                        throw Unsupported($"services.{service}.environment.{key}", "service URLs require a declared ports/expose target and no user info");
                    }
                }
            }
        }
        ValidateAcyclic(definitions);

        var resources = new Dictionary<string, IResourceWithEndpoints>(StringComparer.Ordinal);
        foreach (var (service, definition) in definitions)
        {
            if (options.Replacements.TryGetValue(service, out var replacement))
            {
                resources.Add(service, replacement);
                continue;
            }

            var container = builder.AddContainer($"{name}-{service}", definition.Image)
                .WithContainerNetworkAlias(service);
            if (definition.Command is not null)
            {
                container.WithArgs(definition.Command);
            }
            foreach (var port in definition.Ports)
            {
                container.WithEndpoint(port: port.Published, targetPort: port.Target, name: EndpointName(port.Target),
                    scheme: port.Scheme);
            }
            foreach (var mount in definition.Mounts)
            {
                container.WithBindMount(mount.Source, mount.Target, mount.ReadOnly);
            }
            resources.Add(service, container.Resource);
        }

        foreach (var (service, definition) in definitions)
        {
            var resource = resources[service];
            var environment = builder.CreateResourceBuilder((IResourceWithEnvironment)resource);
            foreach (var (key, value) in definition.Environment)
            {
                if (TryServiceUri(value, definitions, out var uri))
                {
                    var endpoint = new EndpointReference(resources[uri.Host], EndpointName(uri.Port));
                    // A Compose URL such as redis://cache:6379/0 must resolve differently
                    // for a local project and a container. Keep the original protocol/path.
                    environment.WithEnvironment(key, ReferenceExpression.Create(
                        $"{uri.Scheme}://{endpoint.Property(EndpointProperty.Host)}:{endpoint.Property(EndpointProperty.Port)}{UriSuffix(value)}"));
                }
                else
                {
                    environment.WithEnvironment(key, value);
                }
            }
            var waiting = builder.CreateResourceBuilder((IResourceWithWaitSupport)resource);
            foreach (var dependency in definition.Dependencies)
            {
                waiting.WaitForStart(builder.CreateResourceBuilder(resources[dependency]));
            }
        }
        return new DockerComposeImport(builder, resources);
    }

    private static ServiceDefinition ParseService(string name, JsonElement service)
    {
        var path = $"services.{name}";
        ValidateKeys(service, path, "image", "command", "entrypoint", "environment", "ports", "expose", "depends_on", "networks", "volumes");
        if (service.TryGetProperty("entrypoint", out var entrypoint) && entrypoint.ValueKind != JsonValueKind.Null)
        {
            throw Unsupported($"{path}.entrypoint", "entrypoint overrides are not supported");
        }
        if (!service.TryGetProperty("image", out var image) || string.IsNullOrWhiteSpace(image.GetString()))
        {
            throw Unsupported($"{path}.image", "an image is required; prebuild images outside the AppHost");
        }
        if (service.TryGetProperty("networks", out var networks))
        {
            ValidateKeys(networks, $"{path}.networks", "default");
            if (!networks.TryGetProperty("default", out var network) || network.ValueKind != JsonValueKind.Null)
            {
                throw Unsupported($"{path}.networks", "only the implicit default network is supported");
            }
        }
        string[]? command = null;
        if (service.TryGetProperty("command", out var commandNode))
        {
            if (commandNode.ValueKind != JsonValueKind.Array || commandNode.GetArrayLength() == 0)
            {
                throw Unsupported($"{path}.command", "only nonempty normalized argument arrays are supported");
            }
            command = commandNode.EnumerateArray().Select(a => a.GetString()!).ToArray();
        }
        var environment = new Dictionary<string, string>(StringComparer.Ordinal);
        if (service.TryGetProperty("environment", out var env))
        {
            foreach (var variable in env.EnumerateObject())
            {
                if (variable.Value.ValueKind != JsonValueKind.String)
                {
                    throw Unsupported($"{path}.environment.{variable.Name}", "environment values must resolve to strings");
                }
                environment.Add(variable.Name, variable.Value.GetString()!);
            }
        }
        var ports = new Dictionary<int, PortDefinition>();
        if (service.TryGetProperty("ports", out var publishedPorts))
        {
            foreach (var port in publishedPorts.EnumerateArray())
            {
                ValidateKeys(port, $"{path}.ports", "target", "published", "protocol", "mode", "host_ip", "name", "app_protocol");
                if ((port.TryGetProperty("protocol", out var protocol) && protocol.GetString() != "tcp") ||
                    (port.TryGetProperty("mode", out var mode) && mode.GetString() != "ingress") ||
                    (port.TryGetProperty("host_ip", out var ip) && ip.GetString() is not ("" or "127.0.0.1")))
                {
                    throw Unsupported($"{path}.ports", "only TCP, ingress mode and default/127.0.0.1 host binding are supported");
                }
                // Names and app_protocol have semantic meaning; do not accept and discard them.
                if (port.TryGetProperty("name", out _) || port.TryGetProperty("app_protocol", out _))
                {
                    throw Unsupported($"{path}.ports", "named ports and app_protocol are not supported");
                }
                var target = ParsePort(port.GetProperty("target").ToString(), $"{path}.ports.target");
                int? published = port.TryGetProperty("published", out var publishedNode)
                    ? ParsePort(publishedNode.ToString(), $"{path}.ports.published") : null;
                if (!ports.TryAdd(target, new(target, published, target is 80 or 8080 ? "http" : target == 443 ? "https" : "tcp")))
                {
                    throw Unsupported($"{path}.ports", $"multiple bindings for target {target} are unsupported");
                }
            }
        }
        if (service.TryGetProperty("expose", out var exposedPorts))
        {
            foreach (var port in exposedPorts.EnumerateArray())
            {
                var text = port.ToString();
                var target = ParsePort(text.EndsWith("/tcp", StringComparison.Ordinal) ? text[..^4] : text, $"{path}.expose");
                ports.TryAdd(target, new(target, null, target is 80 or 8080 ? "http" : target == 443 ? "https" : "tcp"));
            }
        }
        var dependencies = new List<string>();
        if (service.TryGetProperty("depends_on", out var dependsOn))
        {
            foreach (var dependency in dependsOn.EnumerateObject())
            {
                var dependencyPath = $"{path}.depends_on.{dependency.Name}";
                ValidateKeys(dependency.Value, dependencyPath, "condition", "required", "restart");
                if (dependency.Value.GetProperty("condition").GetString() != "service_started" ||
                    (dependency.Value.TryGetProperty("required", out var required) && !required.GetBoolean()) ||
                    (dependency.Value.TryGetProperty("restart", out var restart) && restart.GetBoolean()))
                {
                    throw Unsupported(dependencyPath, "only required service_started dependencies without restart propagation are supported");
                }
                dependencies.Add(dependency.Name);
            }
        }
        var mounts = new List<MountDefinition>();
        if (service.TryGetProperty("volumes", out var volumes))
        {
            foreach (var mount in volumes.EnumerateArray())
            {
                ValidateKeys(mount, $"{path}.volumes", "type", "source", "target", "read_only", "bind");
                if (mount.GetProperty("type").GetString() != "bind")
                {
                    throw Unsupported($"{path}.volumes", "only existing bind mounts are supported; named/anonymous volumes are not adopted");
                }
                if (mount.TryGetProperty("bind", out var bind))
                {
                    ValidateKeys(bind, $"{path}.volumes.bind", "create_host_path");
                }
                var source = mount.GetProperty("source").GetString()!;
                if (!Path.IsPathFullyQualified(source) || (!File.Exists(source) && !Directory.Exists(source)))
                {
                    throw Unsupported($"{path}.volumes.source", "bind source must be an existing absolute path");
                }
                mounts.Add(new(source, mount.GetProperty("target").GetString()!,
                    mount.TryGetProperty("read_only", out var readOnly) && readOnly.GetBoolean()));
            }
        }
        return new(image.GetString()!, command, environment, ports.Values.ToArray(), dependencies, mounts);
    }

    private static int ParsePort(string text, string path) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var port) && port is > 0 and <= 65535
            ? port : throw Unsupported(path, "expected a single port between 1 and 65535; ranges and UDP are unsupported");

    private static string EndpointName(int port) => $"port{port.ToString(CultureInfo.InvariantCulture)}";

    private static bool TryServiceUri(string value, Dictionary<string, ServiceDefinition> services, out Uri uri) =>
        Uri.TryCreate(value, UriKind.Absolute, out uri!) && services.ContainsKey(uri.Host);

    private static string UriSuffix(string value)
    {
        // Uri.PathAndQuery normalizes "http://api:8080" to "/", which changes a
        // consumer appending "/orders" into "//orders". Preserve the original suffix.
        var authorityStart = value.IndexOf("://", StringComparison.Ordinal) + 3;
        var suffix = value.AsSpan(authorityStart).IndexOfAny('/', '?', '#');
        return suffix < 0 ? string.Empty : value[(authorityStart + suffix)..];
    }

    private static void ValidateKeys(JsonElement node, string path, params string[] allowed)
    {
        foreach (var property in node.EnumerateObject())
        {
            if (!allowed.Contains(property.Name, StringComparer.Ordinal))
            {
                throw Unsupported($"{path}.{property.Name}", "this Compose field is not supported by the import proof of concept");
            }
        }
    }

    private static void ValidateAcyclic(Dictionary<string, ServiceDefinition> services)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var active = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in services.Keys)
        {
            Visit(name);
        }
        void Visit(string name)
        {
            if (!active.Add(name))
            {
                throw Unsupported($"services.{name}.depends_on", "dependency cycle detected");
            }
            if (visited.Add(name))
            {
                foreach (var dependency in services[name].Dependencies)
                {
                    Visit(dependency);
                }
            }
            active.Remove(name);
        }
    }

    private static NotSupportedException Unsupported(string path, string reason) =>
        new($"Cannot import Compose configuration at '{path}': {reason}.");

    private sealed record ServiceDefinition(string Image, string[]? Command, Dictionary<string, string> Environment,
        PortDefinition[] Ports, List<string> Dependencies, List<MountDefinition> Mounts);
    private sealed record PortDefinition(int Target, int? Published, string Scheme);
    private sealed record MountDefinition(string Source, string Target, bool ReadOnly);
}
