// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Azure;

/// <summary>
/// A resource that represents an Azure PostgreSQL database. This is a child resource of an <see cref="AzurePostgresFlexibleServerResource"/>.
/// </summary>
/// <param name="name">The name of the resource.</param>
/// <param name="databaseName">The database name.</param>
/// <param name="postgresParentResource">The Azure PostgreSQL parent resource associated with this database.</param>
public class AzurePostgresFlexibleServerDatabaseResource(string name, string databaseName, AzurePostgresFlexibleServerResource postgresParentResource)
    : Resource(name), IResourceWithParent<AzurePostgresFlexibleServerResource>, IResourceWithConnectionString
{
    /// <summary>
    /// Gets the parent Azure PostgresSQL resource.
    /// </summary>
    public AzurePostgresFlexibleServerResource Parent { get; } = postgresParentResource ?? throw new ArgumentNullException(nameof(postgresParentResource));

    /// <summary>
    /// Gets the connection string expression for the Postgres database.
    /// </summary>
    public ReferenceExpression ConnectionStringExpression => Parent.GetDatabaseConnectionString(Name, databaseName);

    /// <summary>
    /// Gets the database name.
    /// </summary>
    public string DatabaseName { get; } = ThrowIfNullOrEmpty(databaseName);

    /// <summary>
    /// Gets the inner PostgresDatabaseResource resource.
    /// 
    /// This is set when RunAsContainer is called on the AzurePostgresFlexibleServerResource resource to create a local PostgreSQL container.
    /// </summary>
    internal PostgresDatabaseResource? InnerResource { get; private set; }

    /// <summary>
    /// Gets a value indicating whether the current resource represents a container. If so the actual resource is not running in Azure.
    /// </summary>
    [MemberNotNullWhen(true, nameof(InnerResource))]
    public bool IsContainer => InnerResource is not null;

    /// <summary>
    /// Gets the connection URI expression for the PostgreSQL server.
    /// </summary>
    /// <remarks>
    /// Format: <c>postgresql://{user}:{password}@{host}:{port}/{database}</c>.
    /// </remarks>
    public ReferenceExpression UriExpression =>
        IsContainer ?
            InnerResource.UriExpression :
            ReferenceExpression.Create($"{Parent.UriExpression}/{DatabaseName:uri}");

    /// <summary>
    /// Gets the JDBC connection string for the Azure Postgres Flexible Server database.
    /// </summary>
    /// <remarks>
    /// Format: <c>jdbc:postgresql://{host}:{port}/{database}?sslmode=require&amp;authenticationPluginClassName=com.azure.identity.extensions.jdbc.postgresql.AzurePostgresqlAuthenticationPlugin</c>.
    /// </remarks>
    public ReferenceExpression JdbcConnectionString =>
        IsContainer ?
            InnerResource.JdbcConnectionString :
            Parent.BuildJdbcConnectionString(databaseName);

    /// <inheritdoc />
    public override ResourceAnnotationCollection Annotations => InnerResource?.Annotations ?? base.Annotations;

    private static string ThrowIfNullOrEmpty([NotNull] string? argument, [CallerArgumentExpression(nameof(argument))] string? paramName = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(argument, paramName);
        return argument;
    }

    internal void SetInnerResource(PostgresDatabaseResource innerResource)
    {
        // Copy the annotations to the inner resource before making it the inner resource
        foreach (var annotation in Annotations)
        {
            innerResource.Annotations.Add(annotation);
        }

        InnerResource = innerResource;
    }

    IEnumerable<KeyValuePair<string, ReferenceExpression>> IResourceWithConnectionString.GetConnectionProperties() =>
        Parent.CombineProperties([
            new("DatabaseName", ReferenceExpression.Create($"{DatabaseName}")),
            new("Uri", UriExpression),
            new("JdbcConnectionString", JdbcConnectionString),
    ]);
}
