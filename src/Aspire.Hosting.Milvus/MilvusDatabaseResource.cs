// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Aspire.Hosting.Milvus;

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// A resource that represents a Milvus database. This is a child resource of a <see cref="MilvusServerResource"/>.
/// </summary>
/// <param name="name">The name of the resource.</param>
/// <param name="databaseName">The database name.</param>
/// <param name="parent">The Milvus parent resource associated with this database.</param>
public class MilvusDatabaseResource(string name, string databaseName, MilvusServerResource parent) : Resource(name), IResourceWithParent<MilvusServerResource>, IResourceWithConnectionString
{
    /// <summary>
    /// Gets the parent Milvus container resource.
    /// </summary>
    public MilvusServerResource Parent { get; } = parent ?? throw new ArgumentNullException(nameof(parent));

    /// <summary>
    /// Gets the connection string expression for the Milvus database.
    /// </summary>
    /// <remarks>
    /// Format: <c>Endpoint={uri};Key={token};Database={DatabaseName}</c>.
    /// </remarks>
    public ReferenceExpression ConnectionStringExpression =>
       ReferenceExpression.Create($"{Parent};Database={DatabaseName}");

    /// <summary>
    /// Gets the database name.
    /// </summary>
    public string DatabaseName { get; } = ThrowIfNullOrEmpty(databaseName);

    private static string ThrowIfNullOrEmpty([NotNull] string? argument, [CallerArgumentExpression(nameof(argument))] string? paramName = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(argument, paramName);
        return argument;
    }

    IEnumerable<KeyValuePair<string, ReferenceExpression>> IResourceWithConnectionString.GetConnectionProperties() =>
        Parent.CombineProperties([
            new("DatabaseName", ReferenceExpression.Create($"{DatabaseName}"))
        ]);
}
