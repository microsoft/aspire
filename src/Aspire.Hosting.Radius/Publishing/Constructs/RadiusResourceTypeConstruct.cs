// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIRERADIUS004 // Experimental: ConfigureRadiusInfrastructure escape-hatch construct types are consumed internally by the publisher.

using System.Diagnostics.CodeAnalysis;
using Azure.Provisioning;
using Azure.Provisioning.Primitives;

namespace Aspire.Hosting.Radius.Publishing.Constructs;

/// <summary>
/// Represents a Radius resource type instance (e.g., <c>Radius.Data/redisCaches</c>,
/// <c>Radius.Messaging/rabbitMQQueues</c>) in the Bicep AST.
/// The concrete resource type and API version are passed via the constructor
/// since they vary per Aspire resource mapping.
/// </summary>
[Experimental("ASPIRERADIUS004", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public sealed class RadiusResourceTypeConstruct : ProvisionableResource
{
    private BicepValue<string>? _name;
    private BicepValue<string>? _applicationId;
    private BicepValue<string>? _environmentId;
    private BicepValue<string>? _recipeName;
    private BicepDictionary<object>? _recipeParameters;
    private BicepValue<object>? _userName;
    private BicepValue<object>? _password;
    private BicepValue<object>? _database;

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

    /// <summary>
    /// The administrator user name, written to <c>properties.username</c>.
    /// </summary>
    /// <remarks>
    /// The <c>Radius.*</c> resource-type manifests declare their recipe inputs — <c>username</c>,
    /// <c>password</c>, <c>database</c> — as first-class schema properties on the resource, and the
    /// Kubernetes recipes read them as <c>context.resource.properties.&lt;name&gt;</c>. They are not
    /// <c>properties.recipe.parameters</c> entries: <c>username</c> and <c>password</c> are
    /// <c>required</c> in the schema, so a resource that only carried them as recipe parameters
    /// fails validation before any recipe runs.
    /// See <see href="https://github.com/radius-project/resource-types-contrib/blob/main/Data/postgreSqlDatabases/postgreSqlDatabases.yaml"/>
    /// and its Kubernetes recipe
    /// <see href="https://github.com/radius-project/resource-types-contrib/blob/main/Data/postgreSqlDatabases/recipes/kubernetes/bicep/kubernetes-postgresql.bicep"/>.
    /// </remarks>
    public BicepValue<object> UserName
    {
        get { Initialize(); return _userName!; }
        set { Initialize(); _userName!.Assign(value); }
    }

    /// <summary>
    /// The administrator password, written to <c>properties.password</c>. The manifests mark it
    /// <c>x-radius-sensitive</c>, so Radius encrypts it, redacts it on reads, and hands it to the
    /// recipe decrypted.
    /// </summary>
    public BicepValue<object> Password
    {
        get { Initialize(); return _password!; }
        set { Initialize(); _password!.Assign(value); }
    }

    /// <summary>
    /// The database the recipe should provision, written to <c>properties.database</c>.
    /// </summary>
    public BicepValue<object> Database
    {
        get { Initialize(); return _database!; }
        set { Initialize(); _database!.Assign(value); }
    }

    /// <summary>
    /// Assigns one of the schema properties above by its Radius property name, so the publisher can
    /// drive them from the connection-property keys it resolves. Unknown names throw rather than
    /// being dropped: a silently ignored credential is the failure mode this whole path exists to
    /// prevent.
    /// </summary>
    internal void SetSchemaProperty(string propertyName, BicepValue<object> value)
    {
        switch (propertyName)
        {
            case "username":
                UserName = value;
                break;
            case "password":
                Password = value;
                break;
            case "database":
                Database = value;
                break;
            default:
                throw new ArgumentOutOfRangeException(
                    nameof(propertyName),
                    propertyName,
                    "Unknown Radius resource-type schema property.");
        }
    }

    /// <summary>
    /// Reads back a schema property assigned through <see cref="SetSchemaProperty"/>, or
    /// <see langword="null"/> when it was never assigned.
    /// </summary>
    internal BicepValue<object>? GetSchemaProperty(string propertyName) => propertyName switch
    {
        "username" => _userName is null ? null : UserName,
        "password" => _password is null ? null : Password,
        "database" => _database is null ? null : Database,
        _ => throw new ArgumentOutOfRangeException(
            nameof(propertyName),
            propertyName,
            "Unknown Radius resource-type schema property."),
    };

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
        _userName = DefineProperty<object>(nameof(UserName), ["properties", "username"]);
        _password = DefineProperty<object>(nameof(Password), ["properties", "password"]);
        _database = DefineProperty<object>(nameof(Database), ["properties", "database"]);
    }
}
