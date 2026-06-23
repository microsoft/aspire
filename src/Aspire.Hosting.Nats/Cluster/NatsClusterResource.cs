// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// A resource that represents a NATS server container.
/// </summary>
/// <param name="name">The name of the resource.</param>
[AspireExport(ExposeProperties = true)]
public class NatsClusterResource(string name) : Resource(name), IResourceWithConnectionString, IResourceWithWaitSupport
{
    /// <summary>
    /// Gets the connection string expression for the NATS cluster.
    /// </summary>
    public ReferenceExpression ConnectionStringExpression => BuildConnectionString();

    /// <summary>
    /// Gets the <see cref="NatsServerResource"/> instances that have been set up as members of this NATS cluster.
    /// </summary>
    public IEnumerable<NatsServerResource> Members => Annotations.OfType<NatsClusterMemberAnnotation>().Select(a => a.Member);

    internal ReferenceExpression BuildConnectionString()
    {
        var builder = new ReferenceExpressionBuilder();

        var membersList = Members.ToList();
        for (var i = 0; i < membersList.Count; i++)
        {
            var member = membersList[i];

            // NOTE: See https://docs.nats.io/using-nats/developer/connecting#connecting-to-clusters
            builder.Append($"{member.ConnectionStringExpression}");

            if (i < membersList.Count - 1)
            {
                builder.AppendLiteral(",");
            }
        }

        return builder.Build();
    }
}
