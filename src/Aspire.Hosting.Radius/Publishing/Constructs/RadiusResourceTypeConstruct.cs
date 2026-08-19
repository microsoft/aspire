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

    // Which schema properties have actually been assigned a value.
    //
    // The backing fields cannot answer this: Azure.Provisioning's DefineProperty returns a non-null
    // BicepValue in an *unset* state, and DefineProvisionableProperties runs on the first
    // Initialize() — which the publisher triggers when it assigns ResourceName, long before any
    // credential is resolved. A `_userName is null` test would therefore always be false and the
    // publish-time guards that depend on it would never fire.
    private readonly HashSet<string> _assignedSchemaProperties = new(StringComparer.Ordinal);

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
    /// The administrator user name, written to <c>properties.username</c>. Internal: assigned
    /// through <see cref="SetSchemaProperty"/> so a generic resource-type construct does not pin
    /// three type-specific property names into its public surface — <c>database</c> means nothing
    /// for a cache or a queue. A <c>ConfigureRadiusInfrastructure</c> callback that needs to set
    /// one can do so through the type's own recipe parameters.
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
    internal BicepValue<object> UserName
    {
        get { Initialize(); return _userName!; }
        set { Initialize(); _userName!.Assign(value); _assignedSchemaProperties.Add("username"); }
    }

    /// <summary>
    /// The administrator password, written to <c>properties.password</c>. The manifests mark it
    /// <c>x-radius-sensitive</c>, so Radius encrypts it, redacts it on reads, and hands it to the
    /// recipe decrypted.
    /// </summary>
    /// <remarks>
    /// Internal for the same reason as <see cref="UserName"/>, and additionally because a settable
    /// public property here would be an unguarded route for a literal credential into
    /// <c>app.bicep</c>: the publisher's own <c>@secure()</c> parameter discipline applies to the
    /// values it resolves, not to a value assigned from a callback.
    /// </remarks>
    internal BicepValue<object> Password
    {
        get { Initialize(); return _password!; }
        set { Initialize(); _password!.Assign(value); _assignedSchemaProperties.Add("password"); }
    }

    /// <summary>
    /// The database the recipe should provision, written to <c>properties.database</c>. Internal
    /// for the same reason as <see cref="UserName"/>.
    /// </summary>
    internal BicepValue<object> Database
    {
        get { Initialize(); return _database!; }
        set { Initialize(); _database!.Assign(value); _assignedSchemaProperties.Add("database"); }
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
    /// <remarks>
    /// Assignment is tracked explicitly rather than inferred from the backing field. The field is
    /// non-<see langword="null"/> from the first <c>Initialize()</c> onwards whether or not a value
    /// was ever assigned, so a field null-check would report every property as present and silently
    /// disable the callers that rely on this returning <see langword="null"/>.
    /// </remarks>
    internal BicepValue<object>? GetSchemaProperty(string propertyName) => propertyName switch
    {
        "username" => _assignedSchemaProperties.Contains("username") ? UserName : null,
        "password" => _assignedSchemaProperties.Contains("password") ? Password : null,
        "database" => _assignedSchemaProperties.Contains("database") ? Database : null,
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
