// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIRECONTAINERSHELLEXECUTION001

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// A resource that represents a Redis resource independent of the hosting model.
/// </summary>
/// <remarks>
/// A resource that represents a Redis resource independent of the hosting model.
/// </remarks>
/// <param name="name">The name of the resource.</param>
[AspireExport(ExposeProperties = true)]
public class RedisResource(string name) : ContainerResource(name), IResourceWithConnectionString
{
    /// <summary>
    /// Initializes a new instance of the <see cref="RedisResource"/> class.
    /// </summary>
    /// <param name="name">The name of the resource.</param>
    /// <param name="password">A parameter that contains the Redis server password.</param>
    public RedisResource(string name, ParameterResource password) : this(name)
    {
        PasswordParameter = password;
        ShellExecution = true;
    }

    internal const string PrimaryEndpointName = "tcp";

    // The non-TLS endpoint if TLS is enabled, otherwise not allocated
    internal const string SecondaryEndpointName = "secondary";

    /// <summary>
    /// The standard URI scheme registered for Redis, similar to http. See: https://github.com/redis/redis-specifications/blob/1252427cdbc497f66a7f8550c6b5f2f35367dc92/uri/redis.txt
    /// </summary>
    internal const string StandardRedisScheme = "redis";

    /// <summary>
    /// The TLS URI scheme registered for Redis, similar to https. See: https://github.com/redis/redis-specifications/blob/1252427cdbc497f66a7f8550c6b5f2f35367dc92/uri/rediss.txt
    /// </summary>
    internal const string TlsRedisScheme = "rediss";

    private EndpointReference? _primaryEndpoint;

    /// <summary>
    /// Gets the primary endpoint for the Redis server.
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
    /// Gets the parameter that contains the Redis server password.
    /// </summary>
    public ParameterResource? PasswordParameter { get; private set; }

    /// <summary>
    /// Indicates whether TLS is enabled for the Redis server.
    /// </summary>
    /// <remarks>
    /// This property proxies through to <see cref="EndpointAnnotation.TlsEnabled"/> on the
    /// <see cref="PrimaryEndpoint"/>. When set to <see langword="true"/>, the connection string
    /// expression dynamically includes <c>,ssl=true</c> and the URI expression uses the
    /// <c>rediss://</c> scheme. This value is resolved lazily at expression evaluation time,
    /// avoiding timing issues when TLS is enabled later in the application lifecycle
    /// (e.g., during the <c>BeforeStartEvent</c>).
    /// </remarks>
    public bool TlsEnabled => PrimaryEndpoint.TlsEnabled;

    /// <summary>
    /// Arguments for the Dockerfile
    /// </summary>
    internal List<string> Args { get; set; } = new();

    private ReferenceExpression BuildConnectionString()
    {
        var builder = new ReferenceExpressionBuilder();
        builder.Append($"{PrimaryEndpoint.Property(EndpointProperty.HostAndPort)}");

        if (PasswordParameter is not null)
        {
            builder.Append($",password={PasswordParameter}");
        }

        builder.Append($"{PrimaryEndpoint.GetTlsValue(
            enabledValue: ReferenceExpression.Create($",ssl=true"),
            disabledValue: ReferenceExpression.Empty)}");

        return builder.Build();
    }

    /// <summary>
    /// Gets the connection string expression for the Redis server.
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

    /// <summary>
    /// Gets the connection string for the Redis server.
    /// </summary>
    /// <param name="cancellationToken"> A <see cref="CancellationToken"/> to observe while waiting for the task to complete.</param>
    /// <returns>A connection string for the redis server in the form "host:port".</returns>
    public ValueTask<string?> GetConnectionStringAsync(CancellationToken cancellationToken = default)
    {
        if (this.TryGetLastAnnotation<ConnectionStringRedirectAnnotation>(out var connectionStringAnnotation))
        {
            return connectionStringAnnotation.Resource.GetConnectionStringAsync(cancellationToken);
        }

        return BuildConnectionString().GetValueAsync(cancellationToken);
    }

    internal void SetPassword(ParameterResource? password)
    {
        PasswordParameter = password;
    }

    /// <summary>
    /// Gets the connection URI expression for the Redis server.
    /// </summary>
    /// <remarks>
    /// Format: <c>redis://[:{password}@]{host}:{port}</c>. The password segment is omitted when no password is configured.
    /// </remarks>
    public ReferenceExpression UriExpression
    {
        get
        {
            var builder = new ReferenceExpressionBuilder();
            builder.Append($"{PrimaryEndpoint.Property(EndpointProperty.Scheme)}");
            builder.AppendLiteral("://");

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
