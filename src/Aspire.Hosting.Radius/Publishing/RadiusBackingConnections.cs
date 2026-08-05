// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Radius.ResourceMapping;
using Azure.Provisioning.Expressions;

namespace Aspire.Hosting.Radius.Publishing;

/// <summary>
/// Projects the values a consumer needs for a Radius <em>backing</em> resource (a cache, database,
/// or queue) out of that resource's own Radius declaration, rather than deriving them from the
/// Aspire endpoints of the container Aspire would have run locally.
/// </summary>
/// <remarks>
/// <para>
/// A backing resource is not deployed as a <c>Radius.Compute/containers</c>; it is provisioned by a
/// Radius <em>recipe</em>. The recipe owns the Kubernetes objects and the credentials, and neither
/// is derivable from the Aspire model:
/// </para>
/// <list type="bullet">
/// <item>The Service name varies per recipe — the contrib Kubernetes recipes name it after
/// <c>context.resource.name</c>, while the legacy <c>local-dev/rediscaches</c> recipe uses a
/// <c>uniqueString</c>-suffixed name. So the container rule <c>{name}-{name}</c> used by
/// <see cref="RadiusServiceDiscovery"/> never addresses a backing resource's Service. The
/// authoritative address is the recipe's own <c>properties.host</c> / <c>properties.port</c>
/// output. See <see href="https://github.com/microsoft/aspire/issues/18935"/>.</item>
/// <item>The recipe generates its own credentials, so the <c>ParameterResource</c> password Aspire
/// generates for local run mode is not the deployed password. For the legacy
/// <c>Applications.*</c> types the deployed value is read back with the type's <c>listSecrets()</c>
/// action. For the <c>Radius.*</c> UDTs there is no <c>listSecrets()</c>; instead username and
/// password are <em>required schema properties</em> on the resource itself (the recipe reads them as
/// <c>context.resource.properties.&lt;name&gt;</c>), so Aspire writes its own parameter there and
/// both sides then agree by construction (see
/// <c>RadiusInfrastructureBuilder.ApplyRecipeInputPropertyCredentialsAsync</c>).</item>
/// </list>
/// <para>
/// Projections are keyed by the <em>emitted</em> Radius type string rather than the Aspire CLR
/// type, because the legacy and UDT schemas for the same Aspire resource expose different property
/// names and different secret mechanisms.
/// </para>
/// </remarks>
internal static class RadiusBackingConnections
{
    /// <summary>
    /// How a Radius type's credentials reach the consumer. Deliberately a closed hierarchy rather
    /// than a pair of loosely-related fields: the previous shape
    /// (<c>string? PasswordSecret</c> plus <c>bool TakesCredentialRecipeParameters</c>) could
    /// express "handled by neither mechanism", and a row in that state fell through to normal
    /// parameter routing and re-emitted the local password Aspire generated for run mode — the very
    /// defect <see href="https://github.com/microsoft/aspire/issues/18935"/> reports. Making that
    /// state unrepresentable means a new row cannot silently regress.
    /// </summary>
    internal abstract record RadiusCredentialMode
    {
        // Private constructor closes the hierarchy to the nested cases below, so `switch` over them
        // is exhaustive and a future contributor must pick one deliberately.
        private RadiusCredentialMode()
        {
        }

        /// <summary>The recipe generates the credential and exposes it through <c>listSecrets()</c>.</summary>
        internal sealed record ListSecrets(string PasswordSecretName) : RadiusCredentialMode;

        /// <summary>
        /// <c>username</c>/<c>password</c> are required schema properties on the resource, so Aspire
        /// writes its own parameters there and both sides agree by construction. They are not
        /// <c>properties.recipe.parameters</c>: the manifests mark them <c>required</c> and the
        /// recipes read <c>context.resource.properties.&lt;name&gt;</c>, so a resource carrying them
        /// only as recipe parameters fails schema validation before any recipe runs.
        /// </summary>
        internal sealed record RecipeInputProperties : RadiusCredentialMode;

        /// <summary>
        /// The type carries no credential Aspire can project. <paramref name="Reason"/> is written
        /// into the publish-time error so the omission is explained rather than merely observed.
        /// </summary>
        internal sealed record NotProjected(string Reason) : RadiusCredentialMode;
    }

    /// <summary>
    /// Describes how one emitted Radius type surfaces the values a consumer needs.
    /// </summary>
    /// <param name="HostProperty">Name of the non-secret property carrying the host/FQDN, or
    /// <see langword="null"/> when the type does not expose one.</param>
    /// <param name="PortProperty">Name of the non-secret property carrying the port, or
    /// <see langword="null"/> when the type does not expose one.</param>
    /// <param name="UserNameProperty">Name of the non-secret property carrying the user name, or
    /// <see langword="null"/> when the type does not expose one. This is a plain property rather
    /// than a <c>listSecrets()</c> key: the legacy <c>Applications.Datastores/mongoDatabases</c> and
    /// <c>Applications.Messaging/rabbitMQQueues</c> types return only <c>connectionString</c> and
    /// <c>password</c> from <c>listSecrets()</c>, and expose <c>username</c> at
    /// <c>properties.username</c>.</param>
    /// <param name="Credentials">How the credential reaches the consumer.</param>
    internal sealed record RadiusConnectionSchema(
        string? HostProperty,
        string? PortProperty,
        string? UserNameProperty,
        RadiusCredentialMode Credentials);

    // Declared before s_schemas: static field initializers run in textual order, so a shared
    // instance referenced from the table must already be assigned.
    private static readonly RadiusConnectionSchema s_daprNotProjected = new(
        HostProperty: null,
        PortProperty: null,
        UserNameProperty: null,
        new RadiusCredentialMode.NotProjected(
            "Dapr building blocks are reached through the Dapr sidecar's component configuration " +
            "rather than a host, port, or credential Aspire can compose into a connection string."));

    // Schema shapes verified against the Radius TypeSpec definitions for the legacy portable types
    // (radius-project/radius, typespec/Applications.Datastores/*.tsp and
    // typespec/Applications.Messaging/rabbitMQQueues.tsp) and the UDT manifests in
    // radius-project/resource-types-contrib (Data/*, Messaging/*).
    //
    // Keyed by the *emitted* type, i.e. what ResourceTypeMapper.MapResource returns. Every backing
    // type that mapper can emit must appear here; BackingResourceContractTests enforces that,
    // so dropping a LegacyFallbackType without adding the corresponding UDT row fails at test time
    // rather than silently emitting Aspire's local password at deploy time.
    private static readonly Dictionary<string, RadiusConnectionSchema> s_schemas = new(StringComparer.Ordinal)
    {
        // Legacy portable types expose host/port as plain properties and their credentials through
        // a first-class listSecrets() action. Redis has no user name; Mongo and RabbitMQ do.
        [RadiusResourceTypes.LegacyRedisCaches] =
            new("host", "port", null, new RadiusCredentialMode.ListSecrets("password")),
        [RadiusResourceTypes.LegacyMongoDatabases] =
            new("host", "port", "username", new RadiusCredentialMode.ListSecrets("password")),
        [RadiusResourceTypes.LegacyRabbitMQQueues] =
            new("host", "port", "username", new RadiusCredentialMode.ListSecrets("password")),

        // UDTs expose readOnly host/port but no listSecrets(); username/password are required
        // schema properties and the password is redacted on read (x-radius-sensitive), so the only
        // consistent value is the parameter Aspire itself writes onto the resource. The user name is
        // an *input* here, not a readable output, so there is no UserNameProperty.
        [RadiusResourceTypes.PostgreSqlDatabases] =
            new("host", "port", null, new RadiusCredentialMode.RecipeInputProperties()),
        [RadiusResourceTypes.SqlDatabases] =
            new("host", "port", null, new RadiusCredentialMode.RecipeInputProperties()),

        // Dapr types are backing resources by classification but are consumed through the Dapr
        // sidecar's component configuration, not through an address or credential Aspire composes.
        // They are listed explicitly so the schema table stays total over everything
        // ResourceTypeMapper classifies as a backing resource.
        [RadiusResourceTypes.LegacyDaprStateStores] = s_daprNotProjected,
        [RadiusResourceTypes.LegacyDaprPubSubBrokers] = s_daprNotProjected,
        [RadiusResourceTypes.DaprStateStores] = s_daprNotProjected,
        [RadiusResourceTypes.DaprPubSubBrokers] = s_daprNotProjected,

        // Deliberately absent: Radius.Data/redisCaches, Radius.Data/mongoDatabases and
        // Radius.Messaging/rabbitMQQueues. Those UDT manifests were never verified to publish the
        // host/port outputs and credential inputs this projection needs, and today they are
        // unreachable because ResourceTypeMapper always emits the legacy type for Redis, Mongo and
        // RabbitMQ. Leaving them out means a future migration that drops the legacy fallback fails
        // loudly here instead of quietly reverting to the pre-fix behaviour.
    };

    /// <summary>
    /// Gets the connection schema for an emitted Radius type, or <see langword="null"/> when the
    /// type is not a known backing resource (e.g. <c>Radius.Compute/containers</c>).
    /// </summary>
    public static RadiusConnectionSchema? GetSchema(string radiusType) =>
        s_schemas.TryGetValue(radiusType, out var schema) ? schema : null;

    /// <summary>
    /// The emitted Radius types this table describes. Used by the schema guard test to assert the
    /// table stays total over <see cref="ResourceTypeMapper"/>'s backing-resource classification.
    /// </summary>
    public static IReadOnlyCollection<string> KnownTypes => s_schemas.Keys;

    /// <summary>
    /// Builds <c>{identifier}.properties.{propertyName}</c>.
    /// </summary>
    public static BicepExpression Property(string bicepIdentifier, string propertyName) =>
        new MemberExpression(
            new MemberExpression(new IdentifierExpression(bicepIdentifier), "properties"),
            propertyName);

    /// <summary>
    /// Builds <c>{identifier}.listSecrets().{secretName}</c>.
    /// </summary>
    /// <remarks>
    /// <c>listSecrets()</c> is a first-class action on the legacy <c>Applications.*</c> portable
    /// types (see the generated Bicep types under
    /// <c>hack/bicep-types-radius/generated/applications/applications.datastores</c>) and is the
    /// documented way to read recipe-generated credentials at deploy time. The <c>Radius.*</c> UDTs
    /// deliberately do not have it, which is why they use
    /// <see cref="RadiusCredentialMode.RecipeInputProperties"/> instead.
    /// </remarks>
    public static BicepExpression Secret(string bicepIdentifier, string secretName) =>
        new MemberExpression(
            new FunctionCallExpression(
                new MemberExpression(new IdentifierExpression(bicepIdentifier), "listSecrets")),
            secretName);

    /// <summary>
    /// Wraps an expression in Bicep's <c>uriComponent(...)</c> percent-encoding.
    /// </summary>
    /// <remarks>
    /// Used for values a <c>ReferenceExpression</c> declared with the <c>uri</c> string
    /// format. Aspire applies <c>Uri.EscapeDataString</c> when it resolves such a value itself, but
    /// the publisher emits a Bicep expression whose value is only known at deploy time, so the
    /// escaping has to be emitted as a call. This matters most for a recipe-generated password: it
    /// is drawn from an alphabet the recipe chooses, not Aspire's URL-safe generated one, so an
    /// unescaped <c>@</c>, <c>:</c>, or <c>/</c> would truncate or corrupt the URI it lands in.
    /// See <see href="https://learn.microsoft.com/azure/azure-resource-manager/bicep/bicep-functions-string#uricomponent"/>.
    /// </remarks>
    public static BicepExpression UriComponent(BicepExpression expression) =>
        new FunctionCallExpression(new IdentifierExpression("uriComponent"), expression);

    /// <summary>
    /// Wraps an expression in Bicep's <c>string(...)</c> conversion.
    /// </summary>
    /// <remarks>
    /// Container environment variable values are typed <c>string</c>, but a Radius type's
    /// <c>port</c> output is an <c>int</c>. Assigning it bare (<c>value: cache.properties.port</c>)
    /// is a Bicep type error. Inside a string interpolation the conversion is implicit, so this is
    /// only needed when the projected value stands alone.
    /// </remarks>
    public static BicepExpression ToStringExpression(BicepExpression expression) =>
        new FunctionCallExpression(new IdentifierExpression("string"), expression);
}
