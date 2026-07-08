// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Azure.Azd;

/// <summary>
/// Represents a parsed Azure Developer CLI (azd) project, as described by an <c>azure.yaml</c> file.
/// </summary>
/// <remarks>
/// This is a tolerant, read-only projection of the <c>azure.yaml</c> schema
/// (<see href="https://github.com/Azure/azure-dev/blob/main/schemas/v1.0/azure.yaml.json"/>).
/// Unknown or unsupported fields are preserved where practical and surfaced through diagnostics
/// during import rather than causing a parse failure, so that future azd schema additions do not
/// break adoption of an existing project.
/// </remarks>
public sealed class AzdProject
{
    /// <summary>
    /// Gets the name of the azd project. Corresponds to the top-level <c>name</c> field.
    /// </summary>
    public string? Name { get; internal set; }

    /// <summary>
    /// Gets the target resource group name, if specified. Corresponds to the <c>resourceGroup</c> field.
    /// </summary>
    public string? ResourceGroup { get; internal set; }

    /// <summary>
    /// Gets the infrastructure configuration. Corresponds to the <c>infra</c> field.
    /// </summary>
    /// <value>
    /// The parsed <see cref="AzdInfra"/> describing where the project's Bicep (or other IaC) lives,
    /// or <see langword="null"/> when the project relies on azd defaults.
    /// </value>
    public AzdInfra? Infra { get; internal set; }

    /// <summary>
    /// Gets the services defined by the project. Corresponds to the <c>services</c> map.
    /// </summary>
    /// <value>
    /// A dictionary keyed by the service name (the YAML map key), where each value describes a
    /// deployable unit such as a .NET project, container image, or Dockerfile build.
    /// </value>
    public IReadOnlyDictionary<string, AzdService> Services { get; internal set; } = new Dictionary<string, AzdService>();

    /// <summary>
    /// Gets the managed resources defined by the project. Corresponds to the <c>resources</c> map.
    /// </summary>
    /// <value>
    /// A dictionary keyed by the resource name (the YAML map key), where each value describes a
    /// backing Azure resource such as a database, cache, or storage account.
    /// </value>
    public IReadOnlyDictionary<string, AzdResource> Resources { get; internal set; } = new Dictionary<string, AzdResource>();

    /// <summary>
    /// Gets the metadata block associated with the project. Corresponds to the <c>metadata</c> field.
    /// </summary>
    public AzdMetadata? Metadata { get; internal set; }

    /// <summary>
    /// Gets the raw, untyped representation of the parsed <c>azure.yaml</c> document.
    /// </summary>
    /// <remarks>
    /// This is provided as an escape hatch so callers can inspect fields that the typed model does
    /// not yet surface (for example custom <c>pipeline</c>, <c>hooks</c>, or <c>workflows</c> sections).
    /// </remarks>
    public IReadOnlyDictionary<string, object?> Raw { get; internal set; } = new Dictionary<string, object?>();
}

/// <summary>
/// Represents the <c>metadata</c> block of an azd project.
/// </summary>
public sealed class AzdMetadata
{
    /// <summary>
    /// Gets the template identifier the project was created from, if any.
    /// </summary>
    public string? Template { get; internal set; }
}

/// <summary>
/// Represents the <c>infra</c> block of an azd project, describing where its infrastructure-as-code lives.
/// </summary>
public sealed class AzdInfra
{
    /// <summary>
    /// Gets the infrastructure provider (for example <c>bicep</c> or <c>terraform</c>). Defaults to <c>bicep</c> in azd.
    /// </summary>
    public string? Provider { get; internal set; }

    /// <summary>
    /// Gets the directory, relative to the project root, that contains the infrastructure files.
    /// </summary>
    /// <value>The configured path, or <see langword="null"/> when azd's default of <c>infra</c> applies.</value>
    public string? Path { get; internal set; }

    /// <summary>
    /// Gets the name of the entry-point infrastructure module (without extension).
    /// </summary>
    /// <value>The configured module name, or <see langword="null"/> when azd's default of <c>main</c> applies.</value>
    public string? Module { get; internal set; }
}

/// <summary>
/// Represents a single entry in the <c>services</c> map of an azd project.
/// </summary>
/// <remarks>
/// A service is a deployable unit. The <see cref="Host"/> field determines the Azure compute target
/// (for example Azure Container Apps or Azure App Service), while <see cref="Project"/>,
/// <see cref="Image"/>, and <see cref="Docker"/> describe how the workload is built.
/// </remarks>
public sealed class AzdService
{
    /// <summary>
    /// Gets the path, relative to the project root, to the service's source (a directory or project file).
    /// Corresponds to the <c>project</c> field.
    /// </summary>
    public string? Project { get; internal set; }

    /// <summary>
    /// Gets the azd host kind for the service (for example <c>containerapp</c>, <c>appservice</c>,
    /// <c>function</c>, <c>staticwebapp</c>, or <c>aks</c>). Corresponds to the required <c>host</c> field.
    /// </summary>
    public string? Host { get; internal set; }

    /// <summary>
    /// Gets the implementation language (for example <c>dotnet</c>, <c>csharp</c>, <c>js</c>, <c>ts</c>,
    /// <c>python</c>, or <c>docker</c>). Corresponds to the <c>language</c> field.
    /// </summary>
    public string? Language { get; internal set; }

    /// <summary>
    /// Gets a prebuilt container image reference, when the service is deployed from an existing image
    /// rather than built from source. Corresponds to the <c>image</c> field.
    /// </summary>
    public string? Image { get; internal set; }

    /// <summary>
    /// Gets the Docker build configuration, when the service is built from a Dockerfile.
    /// Corresponds to the <c>docker</c> field.
    /// </summary>
    public AzdDocker? Docker { get; internal set; }

    /// <summary>
    /// Gets the names of resources or services that this service depends on. Corresponds to the <c>uses</c> field.
    /// </summary>
    /// <value>
    /// During import each referenced name is wired up as an Aspire reference so connection
    /// information flows into the service's environment.
    /// </value>
    public IReadOnlyList<string> Uses { get; internal set; } = [];

    /// <summary>
    /// Gets the environment variables declared on the service. Corresponds to the <c>env</c> field.
    /// </summary>
    public IReadOnlyDictionary<string, string?> Env { get; internal set; } = new Dictionary<string, string?>();
}

/// <summary>
/// Represents the <c>docker</c> build configuration of an azd service.
/// </summary>
public sealed class AzdDocker
{
    /// <summary>
    /// Gets the Docker build context path, relative to the service's <see cref="AzdService.Project"/> directory.
    /// </summary>
    public string? Context { get; internal set; }

    /// <summary>
    /// Gets the path to the Dockerfile, relative to the service's <see cref="AzdService.Project"/> directory.
    /// </summary>
    public string? Path { get; internal set; }

    /// <summary>
    /// Gets the target build stage to use for a multi-stage Dockerfile.
    /// </summary>
    public string? Target { get; internal set; }
}

/// <summary>
/// Represents a single entry in the <c>resources</c> map of an azd project.
/// </summary>
/// <remarks>
/// Resources are backing Azure services (databases, caches, storage, messaging, AI, and so on). The
/// <see cref="Type"/> field selects the resource kind, and <see cref="Properties"/> exposes the
/// type-specific configuration that azd reads inline (for example a model name or a set of queues).
/// </remarks>
public sealed class AzdResource
{
    /// <summary>
    /// Gets the azd resource type (for example <c>db.redis</c>, <c>db.postgres</c>, <c>storage</c>,
    /// <c>keyvault</c>, <c>messaging.servicebus</c>, or <c>ai.openai.model</c>). Corresponds to the <c>type</c> field.
    /// </summary>
    public string? Type { get; internal set; }

    /// <summary>
    /// Gets the explicit resource name, when the YAML overrides the map key. Corresponds to the <c>name</c> field.
    /// </summary>
    public string? Name { get; internal set; }

    /// <summary>
    /// Gets the names of other resources this resource depends on. Corresponds to the <c>uses</c> field.
    /// </summary>
    public IReadOnlyList<string> Uses { get; internal set; } = [];

    /// <summary>
    /// Gets a value indicating whether the resource refers to an already-existing Azure resource.
    /// Corresponds to the <c>existing</c> field.
    /// </summary>
    public bool Existing { get; internal set; }

    /// <summary>
    /// Gets the type-specific configuration declared inline on the resource.
    /// </summary>
    /// <value>
    /// A dictionary of the remaining YAML keys (those not represented by a dedicated property),
    /// preserving values as parsed (scalars, lists, or nested maps). Resource mappers read this to
    /// reproduce settings such as databases, containers, queues, or model details.
    /// </value>
    public IReadOnlyDictionary<string, object?> Properties { get; internal set; } = new Dictionary<string, object?>();
}
