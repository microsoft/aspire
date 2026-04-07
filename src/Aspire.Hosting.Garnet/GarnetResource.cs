// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// A resource that represents a Garnet resource independent of the hosting model.
/// </summary>
/// <param name="name">The name of the resource.</param>
[AspireExport(ExposeProperties = true)]
public class GarnetResource(string name) : ContainerResource(name), IResourceWithConnectionString
{
    /// <summary>
    /// Initializes a new instance of the <see cref="GarnetResource"/> class.
    /// </summary>
    /// <param name="name">The name of the resource.</param>
    /// <param name="password">A parameter that contains the Garnet server password.</param>
    public GarnetResource(string name, ParameterResource password) : this(name)
    {
        PasswordParameter = password;
    }

    internal const string PrimaryEndpointName = "tcp";

    private EndpointReference? _primaryEndpoint;

    /// <summary>
    /// Gets the primary endpoint for the Garnet server.
    /// </summary>
    public EndpointReference PrimaryEndpoint => _primaryEndpoint ??= new(this, PrimaryEndpointName);

    /// <summary>
    /// Gets the host endpoint reference for this resource.
    /// </summary>
    public EndpointReferenceExpression Host => PrimaryEndpoint.Property(EndpointProperty.Host);

    /// <summary>
    /// Gets the port endpoint reference for this resource.
    /// </summary>
    public EndpointReferenceExpression Port => PrimaryEndpoint.Property(EndpointProperty.Port);

    /// <summary>
    /// Gets the parameter that contains the Garnet server password.
    /// </summary>
    public ParameterResource? PasswordParameter { get; }

    /// <summary>
    /// Gets the connection string expression for the Garnet server.
    /// </summary>
    public ReferenceExpression ConnectionStringExpression
    {
        get
        {
            if (this.TryGetLastAnnotation<ConnectionStringRedirectAnnotation>(out var connectionStringAnnotation))
            {
                return connectionStringAnnotation.Resource.ConnectionStringExpression;
            }

            return BuildConnectionString();
        }
    }

    private ReferenceExpression BuildConnectionString()
    {
        var builder = new ReferenceExpressionBuilder();
        builder.Append($"{PrimaryEndpoint.Property(EndpointProperty.HostAndPort)}");

        if (PasswordParameter is not null)
        {
            builder.Append($",password={PasswordParameter}");
        }

        return builder.Build();
    }

    /// <summary>
    /// Gets the connection URI expression for the Garnet server.
    /// </summary>
    /// <remarks>
    /// Format: <c>redis://[:{password}@]{host}:{port}</c>. The password segment is omitted when no password is configured.
    /// </remarks>
    public ReferenceExpression UriExpression
    {
        get
        {
            var builder = new ReferenceExpressionBuilder();
            builder.AppendLiteral("redis://");

            if (PasswordParameter is not null)
            {
                builder.Append($":{PasswordParameter:uri}@");
            }

            builder.Append($"{Host}");
            builder.AppendLiteral(":");
            builder.Append($"{Port}");

            return builder.Build();
        }
    }

    IEnumerable<KeyValuePair<string, ReferenceExpression>> IResourceWithConnectionString.GetConnectionProperties()
    {
        yield return new("Host", ReferenceExpression.Create($"{Host}"));
        yield return new("Port", ReferenceExpression.Create($"{Port}"));

        if (PasswordParameter is not null)
        {
            yield return new("Password", ReferenceExpression.Create($"{PasswordParameter}"));
        }

        yield return new("Uri", UriExpression);
    }
}
