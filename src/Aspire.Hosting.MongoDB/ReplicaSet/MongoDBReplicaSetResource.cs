// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
using System.Diagnostics.CodeAnalysis;

#pragma warning disable ASPIREMONGODB001

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Represents a MongoDB replica set resource in the application model.
/// A replica set is a group of MongoDB servers that maintain the same data set, providing redundancy and high availability.
/// </summary>
/// <remarks>
/// This resource is a logical grouping of multiple <see cref="MongoDBServerResource"/> instances that are configured as members of the same replica set.
/// </remarks>
/// <param name="name">The name of the resource, which is also the name of the replica set itself.</param>
/// <param name="keyFile">The content of the keyfile the members use to authenticate to each other. Required.</param>
/// <param name="sharedUserName">The user name every member authenticates with, or <see langword="null"/> to use the default user name.</param>
/// <param name="sharedPassword">The password every member authenticates with. Required.</param>
[Experimental("ASPIREMONGODB001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
[AspireExport(ExposeProperties = true)]
public sealed class MongoDBReplicaSetResource(
    string name,
    ParameterResource keyFile,
    ParameterResource? sharedUserName,
    ParameterResource sharedPassword
) : Resource(name), IResourceWithWaitSupport, IResourceWithConnectionString
{
    /// <summary>
    /// Gets the combined connection string for the MongoDB replica set, which includes the endpoints of all members, interpretable by the MongoDB driver.
    /// </summary>
    public ReferenceExpression ConnectionStringExpression => BuildConnectionString();

    /// <summary>
    /// Gets the parameter that contains the content of the key file used for internal authentication between members of the MongoDB replica set.
    /// </summary>
    public ParameterResource SharedKeyFileParameter { get; } = keyFile ?? throw new ArgumentNullException(nameof(keyFile));

    /// <summary>
    /// Gets the parameter that contains the username for authenticating to the MongoDB replica set.
    /// </summary>
    /// <remarks>
    /// This will be the same across all members of the replica set, and is used in conjunction with <see cref="SharedPasswordParameter"/> for authentication.
    /// </remarks>
    public ParameterResource? SharedUserNameParameter { get; } = sharedUserName;

    /// <summary>
    /// Gets the parameter that contains the password for authenticating to the MongoDB replica set.
    /// </summary>
    /// <remarks>
    /// This will be the same across all members of the replica set, and is used in conjunction with <see cref="SharedUserNameParameter"/> for authentication.
    /// </remarks>
    public ParameterResource SharedPasswordParameter { get; } = sharedPassword ?? throw new ArgumentNullException(nameof(sharedPassword));

    /// <summary>
    /// Gets a reference to the username for the MongoDB replica set.
    /// </summary>
    public ReferenceExpression SharedUserNameReference =>
        SharedUserNameParameter is null
            ? ReferenceExpression.Create($"{MongoDBServerResource.DefaultUserName}")
            : ReferenceExpression.Create($"{SharedUserNameParameter}");

    /// <summary>
    /// The members that are currently stopped. The replica set has no process of its own for the orchestrator to follow,
    /// so its state is derived from the state of its members.
    /// </summary>
    internal HashSet<string> StoppedMembers { get; } = new(StringComparers.ResourceName);

    /// <summary>
    /// Whether the replica set has been configured, meaning it can be reported as running again if its members come back.
    /// </summary>
    internal bool IsConfigured { get; set; }

    /// <summary>
    /// Gets the MongoDB server resources that are members of this replica set, in the order they were added.
    /// </summary>
    public IEnumerable<MongoDBServerResource> Members => Annotations.OfType<MongoReplicaSetMemberAnnotation>().Select(a => a.Member);

    private ReferenceExpression BuildConnectionString()
    {
        var membersList = Members.ToList();
        if (membersList is [])
        {
            throw new InvalidOperationException($"Cannot build connection string for MongoDB replica set resource '{Name}' because it does not have any members.");
        }

        var builder = new ReferenceExpressionBuilder();

        // Build the seed list `mongodb://host1:port1,host2:port2,.../?replicaSet=<name>` — see https://www.mongodb.com/docs/manual/reference/connection-string/#dns-seedlist-connection-format
        builder.AppendLiteral("mongodb://");

        if (SharedUserNameParameter is not null)
        {
            builder.Append($"{SharedUserNameParameter:uri}:{SharedPasswordParameter:uri}@");
        }
        else
        {
            builder.Append($"{MongoDBServerResource.DefaultUserName:uri}:{SharedPasswordParameter:uri}@");
        }

        for (var i = 0; i < membersList.Count; i++)
        {
            var member = membersList[i];
            builder.Append($"{member.PrimaryEndpoint.Property(EndpointProperty.HostAndPort)}");
            if (i < membersList.Count - 1)
            {
                builder.AppendLiteral(",");
            }
        }

        builder.AppendLiteral($"/?replicaSet={Name}");

        builder.AppendLiteral("&authSource=");
        builder.Append($"{MongoDBServerResource.DefaultAuthenticationDatabase:uri}");
        builder.AppendLiteral("&authMechanism=");
        builder.Append($"{MongoDBServerResource.DefaultAuthenticationMechanism:uri}");

        // NOTE: TLS is turned on lazily (at `BeforeStartEvent` time, once a certificate is known to be available), so the
        // flag has to be resolved lazily here too. All members of a replica set share the same TLS configuration, so the
        // first member's endpoint is representative of the set.
        builder.Append($"{membersList[0].PrimaryEndpoint.GetTlsValue(
            enabledValue: ReferenceExpression.Create($"&tls=true"),
            disabledValue: ReferenceExpression.Empty)}");

        return builder.Build();
    }

    IEnumerable<KeyValuePair<string, ReferenceExpression>> IResourceWithConnectionString.GetConnectionProperties()
    {
        // NOTE: Unlike a single MongoDB server, a replica set has no one host and port to expose: clients are expected to
        // discover the members through the seed list in the connection string, so only the credentials, the replica set
        // name and the full URI are exposed here.
        yield return new("Username", SharedUserNameReference);
        yield return new("Password", ReferenceExpression.Create($"{SharedPasswordParameter}"));
        yield return new("AuthenticationDatabase", ReferenceExpression.Create($"{MongoDBServerResource.DefaultAuthenticationDatabase}"));
        yield return new("AuthenticationMechanism", ReferenceExpression.Create($"{MongoDBServerResource.DefaultAuthenticationMechanism}"));
        yield return new("ReplicaSetName", ReferenceExpression.Create($"{Name}"));
        yield return new("Uri", ConnectionStringExpression);
    }
}
