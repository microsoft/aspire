// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Azure;

/// <summary>
/// A resource that represents an Azure SQL database. This is a child resource of an <see cref="AzureSqlServerResource"/>.
/// </summary>
/// <param name="name">The name of the resource.</param>
/// <param name="databaseName">The database name.</param>
/// <param name="parent">The Azure SQL Database (server) parent resource associated with this database.</param>
public class AzureSqlDatabaseResource(string name, string databaseName, AzureSqlServerResource parent)
    : Resource(name), IResourceWithParent<AzureSqlServerResource>, IResourceWithConnectionString
{
    /// <summary>
    /// SKU associated with the free offer
    /// </summary>
    internal const string FREE_DB_SKU = "GP_S_Gen5_2";

    /// <summary>
    /// Gets the parent Azure SQL Database (server) resource.
    /// </summary>
    public AzureSqlServerResource Parent { get; } = parent ?? throw new ArgumentNullException(nameof(parent));

    /// <summary>
    /// Gets the connection string expression for the Azure SQL database.
    /// </summary>
    public ReferenceExpression ConnectionStringExpression =>
        ReferenceExpression.Create($"{Parent};Database={DatabaseName}");

    /// <summary>
    /// Gets the database name.
    /// </summary>
    public string DatabaseName { get; } = ThrowIfNullOrEmpty(databaseName);

    /// <summary>  
    /// Gets or Sets the database SKU name  
    /// </summary>  
    internal bool UseDefaultAzureSku { get; set; } // Default to false  

    /// <summary>
    /// Gets the inner SqlServerDatabaseResource resource.
    /// 
    /// This is set when RunAsContainer is called on the AzureSqlServerResource resource to create a local SQL Server container.
    /// </summary>
    internal SqlServerDatabaseResource? InnerResource { get; private set; }

    /// <summary>
    /// Gets a value indicating whether the current resource represents a container. If so the actual resource is not running in Azure.
    /// </summary>
    [MemberNotNullWhen(true, nameof(InnerResource))]
    public bool IsContainer => InnerResource is not null;

    /// <summary>
    /// Gets the connection URI expression for the Azure SQL database.
    /// </summary>
    /// <remarks>
    /// Format: <c>mssql://{host}:{port}/{database}</c>.
    /// </remarks>
    public ReferenceExpression UriExpression =>
        IsContainer ?
            InnerResource.UriExpression :
            ReferenceExpression.Create($"{Parent.UriExpression}/{DatabaseName:uri}");

    /// <summary>
    /// Gets the JDBC connection string for the Azure SQL database.
    /// </summary>
    /// <remarks>
    /// Format: <c>jdbc:sqlserver://{host}:{port};database={database};encrypt=true;trustServerCertificate=false</c>.
    /// When running in a container, inherits the container JDBC format including username and password.
    /// </remarks>
    public ReferenceExpression JdbcConnectionString =>
        IsContainer ?
            InnerResource.JdbcConnectionString :
            Parent.BuildJdbcConnectionString(DatabaseName);

    /// <inheritdoc />
    public override ResourceAnnotationCollection Annotations => InnerResource?.Annotations ?? base.Annotations;

    private static string ThrowIfNullOrEmpty([NotNull] string? argument, [CallerArgumentExpression(nameof(argument))] string? paramName = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(argument, paramName);
        return argument;
    }

    internal void SetInnerResource(SqlServerDatabaseResource innerResource)
    {
        // Copy the annotations to the inner resource before making it the inner resource
        foreach (var annotation in Annotations)
        {
            innerResource.Annotations.Add(annotation);
        }

        InnerResource = innerResource;
    }

    IEnumerable<KeyValuePair<string, ReferenceExpression>> IResourceWithConnectionString.GetConnectionProperties() =>
        Parent.CombineProperties(
            [
                new ("DatabaseName", ReferenceExpression.Create($"{DatabaseName}")),
                new ("Uri", UriExpression),
                new ("JdbcConnectionString", JdbcConnectionString),
            ]);
}
