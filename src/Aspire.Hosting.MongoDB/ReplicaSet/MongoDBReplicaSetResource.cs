// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Represents a MongoDB replica set resource in the application model.
/// A replica set is a group of MongoDB servers that maintain the same data set, providing redundancy and high availability.
/// </summary>
/// <remarks>
/// This resource is a logical grouping of multiple <see cref="MongoDBServerResource"/> instances that are configured as members of the same replica set.
/// </remarks>
public sealed class MongoDBReplicaSetResource(
    string name,
    ParameterResource keyFile
) : Resource(name), IResourceWithWaitSupport, IResourceWithConnectionString
{
    /// <summary>
    /// Gets the combined connection string for the MongoDB replica set, which includes the endpoints of all members, interpretable by the MongoDB driver.
    /// </summary>
    public ReferenceExpression ConnectionStringExpression => BuildConnectionString();

    private ReferenceExpression BuildConnectionString()
    {
        if (!this.TryGetAnnotationsOfType<MongoReplicaSetMemberAnnotation>(out var members) || !members.Any())
        {
            throw new InvalidOperationException($"Cannot build connection string for MongoDB replica set resource '{Name}' because it does not have any members.");
        }

        var builder = new ReferenceExpressionBuilder();

        // Build the seed list `mongodb://host1:port1,host2:port2,.../?replicaSet=<name>` — see https://www.mongodb.com/docs/manual/reference/connection-string/#dns-seedlist-connection-format
        builder.AppendLiteral("mongodb://");
        var membersList = members.ToList();
        for (var i = 0; i < membersList.Count; i++)
        {
            builder.Append($"{membersList[i].Member.PrimaryEndpoint.Property(EndpointProperty.HostAndPort)}");
            if (i < membersList.Count - 1)
            {
                builder.AppendLiteral(",");
            }
        }
        builder.AppendLiteral($"/?replicaSet={Name}");

        return builder.Build();
    }

    /// <summary>
    /// Gets the parameter that contains the content of the key file used for internal authentication between members of the MongoDB replica set.
    /// </summary>
    public ParameterResource SharedKeyFileParameter => keyFile;
}
