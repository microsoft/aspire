// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Utils;

namespace Aspire.Hosting;

/// <summary>
/// Composes independently owned services through producer-authored binding contracts.
/// </summary>
public static class ServiceContractResourceBuilderExtensions
{
    private static readonly JsonSerializerOptions s_jsonOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    /// <summary>
    /// Adds a service from a trusted contract artifact, or from an explicitly selected source checkout.
    /// </summary>
    /// <param name="builder">The application builder.</param>
    /// <param name="name">The resource name used for discovery.</param>
    /// <param name="contractPath">The producer's contract artifact, relative to the AppHost directory.</param>
    /// <param name="contractId">The service identity expected by the consumer.</param>
    /// <param name="majorVersion">The contract major version supported by the consumer.</param>
    /// <param name="inputs">Bindings from contract input names to consumer-owned parameters.</param>
    /// <param name="sourceDirectory">An optional, already cloned producer repository, relative to the AppHost directory.</param>
    /// <returns>The underlying project or container, exposing the same named HTTP endpoint in either mode.</returns>
    /// <remarks>
    /// Contracts are trusted execution inputs, like AppHost code: they select an image or a project to run.
    /// Source mode reads <c>aspire-service.json</c> from the checkout rather than reusing stale release metadata.
    /// Both artifacts must match the consumer's identity and major version. This is a binding compatibility
    /// check, not a guarantee of HTTP API compatibility. No repositories are cloned and no nested AppHosts run.
    /// Required inputs remain parameter references, including secrets; their values are not stored in contracts.
    /// <code lang="csharp">
    /// var catalog = builder.AddServiceContract("catalog", "contracts/catalog.json", "contoso.catalog", 1,
    ///     new Dictionary&lt;string, IResourceBuilder&lt;ParameterResource&gt;&gt;
    ///     {
    ///         ["region"] = builder.AddParameter("catalog-region")
    ///     }, builder.Configuration["CatalogSource"]);
    /// builder.AddProject&lt;Projects.Web&gt;("web")
    ///     .WithReference(catalog.GetEndpoint("http"))
    ///     .WaitFor(catalog);
    /// </code>
    /// </remarks>
    [Experimental("ASPIRESERVICECONTRACTS001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    [AspireExportIgnore(Reason = "Experimental .NET service-contract prototype; parameter bindings need a polyglot API design.")]
    public static IResourceBuilder<IResourceWithEndpoints> AddServiceContract(
        this IDistributedApplicationBuilder builder,
        [ResourceName] string name,
        string contractPath,
        string contractId,
        int majorVersion,
        IReadOnlyDictionary<string, IResourceBuilder<ParameterResource>> inputs,
        string? sourceDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(contractPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(contractId);
        ArgumentOutOfRangeException.ThrowIfLessThan(majorVersion, 1);
        ArgumentNullException.ThrowIfNull(inputs);

        var artifactPath = Path.GetFullPath(contractPath, builder.AppHostDirectory);
        var contract = ReadContract(artifactPath, contractId, majorVersion);
        string? projectPath = null;
        if (sourceDirectory is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(sourceDirectory);
            var root = Path.GetFullPath(sourceDirectory, builder.AppHostDirectory);
            contract = ReadContract(Path.Combine(root, "aspire-service.json"), contractId, majorVersion);
            projectPath = PathNormalizer.NormalizePathForCurrentPlatform(Path.Combine(root, contract.Project));
            var relativePath = Path.GetRelativePath(root, projectPath);
            if (Path.IsPathRooted(relativePath) || relativePath == ".." ||
                relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            {
                throw new DistributedApplicationException($"Service contract '{contractId}' project must be inside the selected source directory.");
            }
            if (!File.Exists(projectPath))
            {
                throw new DistributedApplicationException($"Service contract '{contractId}' project '{projectPath}' does not exist. Check the selected checkout and its aspire-service.json project path.");
            }
        }

        // Validate the complete binding before adding anything to the model.
        var requiredInputs = contract.Inputs.Select(input => input.Name).ToHashSet(StringComparer.Ordinal);
        var missing = requiredInputs.Except(inputs.Keys, StringComparer.Ordinal).Order().ToArray();
        var unexpected = inputs.Keys.Except(requiredInputs, StringComparer.Ordinal).Order().ToArray();
        if (missing.Length != 0 || unexpected.Length != 0)
        {
            throw new DistributedApplicationException(
                $"Service contract '{contractId}' input mismatch. Missing: [{string.Join(", ", missing)}]. Unexpected: [{string.Join(", ", unexpected)}].");
        }
        foreach (var (key, input) in inputs)
        {
            if (input is null || !builder.Resources.Contains(input.Resource))
            {
                throw new DistributedApplicationException($"Service contract '{contractId}' input '{key}' must be a parameter from this AppHost.");
            }
        }

        if (projectPath is not null)
        {
            return Configure(builder.AddProject(name, projectPath, launchProfileName: null), targetPort: null);
        }

        var container = builder.CreateResourceBuilder(new ContainerResource(name));
        try
        {
            container.WithImage(contract.Image).WithImageTag(contract.ImageTag);
        }
        catch (ArgumentException exception)
        {
            throw new DistributedApplicationException($"Invalid service contract '{artifactPath}': invalid container image '{contract.Image}'.", exception);
        }
        return Configure(builder.AddResource(container.Resource), contract.Http.TargetPort);

        IResourceBuilder<T> Configure<T>(IResourceBuilder<T> resource, int? targetPort)
            where T : IResourceWithEnvironment, IResourceWithEndpoints
        {
            // An endpoint's env shortcut suppresses .NET URL/Kestrel overrides. Bind the
            // port separately so a source project's configured listener follows DCP's allocation.
            resource.WithHttpEndpoint(targetPort: targetPort, name: "http")
                .WithEnvironment(contract.Http.PortEnvironmentVariable, resource.GetEndpoint("http").Property(EndpointProperty.TargetPort))
                .WithHttpHealthCheck(contract.Http.ReadinessPath, endpointName: "http");
            foreach (var input in contract.Inputs)
            {
                resource.WithEnvironment(input.EnvironmentVariable, inputs[input.Name]);
            }
            return resource;
        }
    }

    private static Contract ReadContract(string path, string expectedId, int expectedMajor)
    {
        if (!File.Exists(path))
        {
            throw new DistributedApplicationException($"Service contract '{path}' does not exist. Obtain the producer's contract artifact or select a checkout containing aspire-service.json.");
        }

        Contract contract;
        try
        {
            using var stream = File.OpenRead(path);
            contract = JsonSerializer.Deserialize<Contract>(stream, s_jsonOptions)
                ?? throw new JsonException("Expected a contract object.");
        }
        catch (JsonException exception)
        {
            throw new DistributedApplicationException($"Invalid service contract '{path}': {exception.Message}", exception);
        }

        // Example: schemaVersion=1 and version="1.2.0". Schema and service API versions evolve separately.
        if (contract.SchemaVersion != 1 ||
            !Version.TryParse(contract.Version, out var version) || version.Major < 1 || version.Build < 0 || version.Revision != -1)
        {
            throw new DistributedApplicationException($"Invalid service contract '{path}': schemaVersion must be 1 and version must be a three-part numeric version with major >= 1.");
        }
        if (contract.Id != expectedId || version.Major != expectedMajor)
        {
            throw new DistributedApplicationException($"Service contract mismatch in '{path}': expected '{expectedId}' major {expectedMajor}, found '{contract.Id}' version '{contract.Version}'.");
        }
        if (string.IsNullOrWhiteSpace(contract.Image) || string.IsNullOrWhiteSpace(contract.ImageTag) ||
            !ContainerReferenceParser.IsValidTag(contract.ImageTag) ||
            contract.ImageTag.Equals("latest", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(contract.Project) || Path.IsPathRooted(contract.Project) ||
            contract.Http is null || contract.Http.TargetPort is < 1 or > 65535 ||
            !IsEnvironmentVariable(contract.Http.PortEnvironmentVariable) ||
            string.IsNullOrWhiteSpace(contract.Http.ReadinessPath) || !contract.Http.ReadinessPath.StartsWith('/') ||
            contract.Http.ReadinessPath.StartsWith("//", StringComparison.Ordinal) ||
            contract.Inputs is null ||
            contract.Inputs.Any(input => input is null || string.IsNullOrWhiteSpace(input.Name) || !IsEnvironmentVariable(input.EnvironmentVariable)) ||
            contract.Inputs.Select(input => input.Name).Distinct(StringComparer.Ordinal).Count() != contract.Inputs.Length ||
            contract.Inputs.Select(input => input.EnvironmentVariable).Append(contract.Http.PortEnvironmentVariable)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count() != contract.Inputs.Length + 1)
        {
            throw new DistributedApplicationException($"Invalid service contract '{path}': require an explicit image/tag (not latest), relative project, HTTP target port, absolute readiness path, and unique input names/environment variables.");
        }
        return contract;
    }

    private static bool IsEnvironmentVariable(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        (char.IsAsciiLetter(value[0]) || value[0] == '_') &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character == '_');

    private sealed class Contract
    {
        public required int SchemaVersion { get; init; }
        public required string Id { get; init; }
        public required string Version { get; init; }
        public required string Image { get; init; }
        public required string ImageTag { get; init; }
        public required string Project { get; init; }
        public required HttpContract Http { get; init; }
        public required InputContract[] Inputs { get; init; }
    }

    private sealed class HttpContract
    {
        public required int TargetPort { get; init; }
        public required string PortEnvironmentVariable { get; init; }
        public required string ReadinessPath { get; init; }
    }

    private sealed class InputContract
    {
        public required string Name { get; init; }
        public required string EnvironmentVariable { get; init; }
    }
}
