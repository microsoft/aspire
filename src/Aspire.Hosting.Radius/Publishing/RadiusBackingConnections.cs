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
/// password are <em>required recipe inputs</em>, so Aspire passes its own parameter in as a recipe
/// parameter and both sides then agree by construction (see
/// <c>RadiusInfrastructureBuilder.ApplyRecipeCredentialParameters</c>).</item>
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
    /// Describes how one emitted Radius type surfaces the values a consumer needs.
    /// </summary>
    /// <param name="HostProperty">Name of the non-secret property carrying the host/FQDN, or
    /// <see langword="null"/> when the type does not expose one.</param>
    /// <param name="PortProperty">Name of the non-secret property carrying the port, or
    /// <see langword="null"/> when the type does not expose one.</param>
    /// <param name="PasswordSecret">Key of the <c>listSecrets()</c> response carrying the password,
    /// or <see langword="null"/> when the type has no <c>listSecrets()</c> action.</param>
    /// <param name="TakesCredentialRecipeParameters">When <see langword="true"/>, the type takes
    /// <c>username</c>/<c>password</c> as required recipe inputs, so Aspire supplies its own
    /// parameters to the recipe instead of reading credentials back off the resource.</param>
    internal readonly record struct RadiusConnectionSchema(
        string? HostProperty,
        string? PortProperty,
        string? PasswordSecret,
        bool TakesCredentialRecipeParameters);

    // Schema shapes verified against the Radius TypeSpec definitions for the legacy portable types
    // (radius-project/radius, typespec/Applications.Datastores/*.tsp and
    // typespec/Applications.Messaging/rabbitMQQueues.tsp) and the UDT manifests in
    // radius-project/resource-types-contrib (Data/*, Messaging/*).
    private static readonly Dictionary<string, RadiusConnectionSchema> s_schemas = new(StringComparer.Ordinal)
    {
        // Legacy portable types expose host/port as plain properties and their credentials through
        // a first-class listSecrets() action.
        [RadiusResourceTypes.LegacyRedisCaches] = new("host", "port", "password", false),
        [RadiusResourceTypes.LegacyMongoDatabases] = new("host", "port", "password", false),
        [RadiusResourceTypes.LegacyRabbitMQQueues] = new("host", "port", "password", false),

        // UDTs expose readOnly host/port but no listSecrets(); username/password are required
        // recipe inputs and the password is redacted on read (x-radius-sensitive), so the only
        // consistent value is the parameter Aspire itself feeds into the recipe.
        [RadiusResourceTypes.RedisCaches] = new("host", "port", null, false),
        [RadiusResourceTypes.PostgreSqlDatabases] = new("host", "port", null, true),
        [RadiusResourceTypes.SqlDatabases] = new("host", "port", null, true),
        [RadiusResourceTypes.MongoDatabases] = new("endpoint", null, null, false),
        [RadiusResourceTypes.RabbitMQQueues] = new("host", null, null, false),
    };

    /// <summary>
    /// Gets the connection schema for an emitted Radius type, or <see langword="null"/> when the
    /// type is not a known backing resource (e.g. <c>Radius.Compute/containers</c> or a Dapr type).
    /// </summary>
    public static RadiusConnectionSchema? GetSchema(string radiusType) =>
        s_schemas.TryGetValue(radiusType, out var schema) ? schema : null;

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
    /// deliberately do not have it, which is why <see cref="RadiusConnectionSchema.PasswordSecret"/>
    /// is <see langword="null"/> for them.
    /// </remarks>
    public static BicepExpression Secret(string bicepIdentifier, string secretName) =>
        new MemberExpression(
            new FunctionCallExpression(
                new MemberExpression(new IdentifierExpression(bicepIdentifier), "listSecrets")),
            secretName);

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
