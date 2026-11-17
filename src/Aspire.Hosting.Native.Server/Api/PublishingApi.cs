// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Nodes;
using Aspire.Hosting.Native.Model;
using Aspire.Hosting.Native.Runtime;

namespace Aspire.Hosting.Native.Api;

internal sealed partial class Composition
{
    /// <summary>Seals and exports desired deployment data, preserving expressions and omitting secret payloads and runtime state.</summary>
    [AspireExport]
    public string ExportPublishModel()
    {
        ObjectDisposedException.ThrowIf(IsRetired, this);
        var snapshot = Model.Seal();
        var names = snapshot.Resources.ToDictionary(resource => resource.Handle, resource => resource.Name);
        JsonObject Expression(SymbolicValue value) => value.Project<JsonObject>(
            text => new() { ["kind"] = "literal", ["value"] = text },
            name => new() { ["kind"] = "parameter", ["name"] = name },
            (resource, endpoint, property) => new()
            {
                ["kind"] = "endpoint", ["resource"] = names[resource], ["endpoint"] = endpoint,
                ["property"] = property.ToString()
            },
            parts => new() { ["kind"] = "composite", ["parts"] = new JsonArray(parts.Cast<JsonNode>().ToArray()) },
            inner => new() { ["kind"] = "uriEncoded", ["value"] = inner });
        var resources = new JsonArray();
        foreach (var resource in snapshot.Resources.OrderBy(resource => resource.Name, StringComparer.Ordinal))
        {
            var workload = resource.Workload;
            var plan = workload?.Plan;
            var symbolic = resource.SymbolicConfiguration.ToDictionary(entry => entry.Name, entry => entry.Value);
            var environment = new JsonObject();
            var arguments = new JsonArray();
            var configuration = new JsonObject(resource.Configuration.Select(entry =>
                KeyValuePair.Create(entry.Name, (JsonNode?)JsonValue.Create(entry.Value))));
            if (resource.ComputeEnvironment is { } environmentHandle)
            {
                configuration["publish.computeEnvironment"] = names[environmentHandle];
            }
            if (workload is not null)
            {
                foreach (var variable in plan!.Environment)
                {
                    environment[variable.Name] = Expression(SymbolicValue.Literal(snapshot.GenerationId, variable.Value));
                }
                foreach (var binding in workload.Bindings.Environment)
                {
                    environment[binding.Name] = Expression(symbolic[binding.ConfigurationKey]);
                }
                for (var index = 0; index < plan.Arguments.Length; index++)
                {
                    var binding = workload.Bindings.Arguments.FirstOrDefault(binding => binding.Index == index);
                    arguments.Add((JsonNode)Expression(binding is null
                        ? SymbolicValue.Literal(snapshot.GenerationId, plan.Arguments[index])
                        : symbolic[binding.ConfigurationKey]));
                }
            }
            resources.Add((JsonNode)new JsonObject
            {
                ["name"] = resource.Name,
                ["type"] = resource.TypeId,
                ["kind"] = plan switch { ContainerWorkload => "container", ExecutableWorkload => "executable", _ => "resource" },
                ["image"] = (plan as ContainerWorkload)?.Image,
                ["entrypoint"] = (plan as ContainerWorkload)?.Command,
                ["executable"] = (plan as ExecutableWorkload)?.ExecutablePath,
                ["workingDirectory"] = (plan as ExecutableWorkload)?.WorkingDirectory,
                ["computeEnvironment"] = resource.ComputeEnvironment is { } computeEnvironment ? names[computeEnvironment] : null,
                ["environment"] = environment,
                ["arguments"] = arguments,
                ["endpoints"] = new JsonArray((plan?.Endpoints ?? []).Select(endpoint => (JsonNode)new JsonObject
                {
                    ["name"] = endpoint.Name, ["protocol"] = endpoint.Protocol.ToString().ToLowerInvariant(),
                    ["scheme"] = endpoint.UriScheme, ["targetPort"] = endpoint.TargetPort,
                    ["isExternal"] = endpoint.IsExternal,
                    ["hostPort"] = endpoint.HostPort, ["environmentVariable"] = endpoint.EnvironmentVariable
                }).ToArray()),
                ["mounts"] = new JsonArray(((plan as ContainerWorkload)?.Mounts ?? []).Select(mount => (JsonNode)new JsonObject
                {
                    ["kind"] = mount.Type.ToString().ToLowerInvariant(), ["source"] = mount.Source,
                    ["target"] = mount.Target, ["readOnly"] = mount.ReadOnly
                }).ToArray()),
                ["dependencies"] = new JsonArray(resource.Dependencies.Select(handle => (JsonNode?)JsonValue.Create(names[handle])).ToArray()),
                ["lifecycle"] = new JsonArray((workload?.Lifecycle ?? []).Select(hook => (JsonNode?)JsonValue.Create(hook.Phase))
                    .Concat(resource.ProviderLifecycle.Select(hook => (JsonNode?)JsonValue.Create("provider"))).ToArray()),
                ["configuration"] = configuration
            });
        }

        return new JsonObject
        {
            ["schemaVersion"] = 1,
            ["parameters"] = new JsonArray(snapshot.SecretParameters.Concat(snapshot.Parameters.Select(parameter => parameter.Value))
                .OrderBy(parameter => parameter.ParameterName, StringComparer.Ordinal)
                .Select(parameter => (JsonNode?)JsonValue.Create(parameter.ParameterName)).ToArray()),
            ["secretParameters"] = new JsonArray(snapshot.SecretParameters.OrderBy(parameter => parameter.ParameterName, StringComparer.Ordinal)
                .Select(parameter => (JsonNode?)JsonValue.Create(parameter.ParameterName)).ToArray()),
            ["parameterDefaults"] = new JsonObject(snapshot.Parameters.Where(parameter => parameter.HasDefault)
                .OrderBy(parameter => parameter.Value.ParameterName, StringComparer.Ordinal).Select(parameter =>
                    KeyValuePair.Create(parameter.Value.ParameterName, (JsonNode?)JsonValue.Create(parameter.Value.Resolve(ValueNetworkContext.Host,
                        (_, _, _) => throw new InvalidOperationException("Parameter defaults cannot contain endpoints.")))))),
            ["resources"] = resources
        }.ToJsonString();
    }
}
