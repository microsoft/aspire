// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Azure.Provisioning;
using Azure.Provisioning.Primitives;

namespace Aspire.Hosting.Radius.Publishing.Constructs;

/// <summary>
/// Represents a Radius resource type instance (e.g., <c>Radius.Data/redisCaches</c>,
/// <c>Radius.Messaging/rabbitMQQueues</c>) in the Bicep AST.
/// The concrete resource type and API version are passed via the constructor
/// since they vary per Aspire resource mapping.
/// </summary>
public sealed class RadiusResourceTypeConstruct : ProvisionableResource
{
    private BicepValue<string>? _name;
    private BicepValue<string>? _applicationId;
    private BicepValue<string>? _environmentId;
    private BicepValue<string>? _recipeName;
    private BicepDictionary<object>? _recipeParameters;
    private BicepValue<string>? _resourceProvisioning;
    private BicepValue<string>? _host;
    private BicepValue<int>? _port;

    /// <summary>The resource name.</summary>
    public BicepValue<string> ResourceName
    {
        get { Initialize(); return _name!; }
        set { Initialize(); _name!.Assign(value); }
    }

    /// <summary>Reference to the application resource ID.</summary>
    public BicepValue<string> ApplicationId
    {
        get { Initialize(); return _applicationId!; }
        set { Initialize(); _applicationId!.Assign(value); }
    }

    /// <summary>Reference to the environment resource ID.</summary>
    public BicepValue<string> EnvironmentId
    {
        get { Initialize(); return _environmentId!; }
        set { Initialize(); _environmentId!.Assign(value); }
    }

    /// <summary>The recipe name (e.g., "default").</summary>
    public BicepValue<string> RecipeName
    {
        get { Initialize(); return _recipeName!; }
        set { Initialize(); _recipeName!.Assign(value); }
    }

    /// <summary>Recipe parameters dictionary for typed parameter values.</summary>
    public BicepDictionary<object> RecipeParameters
    {
        get { Initialize(); return _recipeParameters!; }
        set { Initialize(); _recipeParameters!.Assign(value); }
    }

    /// <summary>Resource provisioning mode (e.g., "manual").</summary>
    public BicepValue<string> ResourceProvisioning
    {
        get { Initialize(); return _resourceProvisioning!; }
        set { Initialize(); _resourceProvisioning!.Assign(value); }
    }

    /// <summary>Host for manually provisioned resources.</summary>
    public BicepValue<string> Host
    {
        get { Initialize(); return _host!; }
        set { Initialize(); _host!.Assign(value); }
    }

    /// <summary>Port for manually provisioned resources.</summary>
    public BicepValue<int> Port
    {
        get { Initialize(); return _port!; }
        set { Initialize(); _port!.Assign(value); }
    }

    /// <summary>
    /// Gets the Radius resource type string (e.g., "Radius.Data/redisCaches").
    /// </summary>
    internal string RadiusType { get; }

    /// <summary>Initializes a new <see cref="RadiusResourceTypeConstruct"/>.</summary>
    /// <param name="bicepIdentifier">The Bicep identifier for the resource.</param>
    /// <param name="resourceType">The Radius resource type (e.g., <c>Radius.Data/redisCaches</c>).</param>
    /// <param name="apiVersion">The resource type API version.</param>
    public RadiusResourceTypeConstruct(string bicepIdentifier, string resourceType, string apiVersion)
        : base(bicepIdentifier, new Azure.Core.ResourceType(resourceType), apiVersion)
    {
        RadiusType = resourceType;
    }

    /// <inheritdoc />
    protected override void DefineProvisionableProperties()
    {
        _name = DefineProperty<string>(nameof(ResourceName), ["name"]);
        _applicationId = DefineProperty<string>(nameof(ApplicationId), ["properties", "application"]);
        _environmentId = DefineProperty<string>(nameof(EnvironmentId), ["properties", "environment"]);
        _recipeName = DefineProperty<string>(nameof(RecipeName), ["properties", "recipe", "name"]);
        _recipeParameters = DefineDictionaryProperty<object>(nameof(RecipeParameters), ["properties", "recipe", "parameters"]);
        _resourceProvisioning = DefineProperty<string>(nameof(ResourceProvisioning), ["properties", "resourceProvisioning"]);
        _host = DefineProperty<string>(nameof(Host), ["properties", "host"]);
        _port = DefineProperty<int>(nameof(Port), ["properties", "port"]);
    }
}
