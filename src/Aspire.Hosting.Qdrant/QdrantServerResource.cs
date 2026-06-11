// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// A resource that represents a Qdrant database.
/// </summary>
[AspireExport(ExposeProperties = true)]
public class QdrantServerResource : ContainerResource, IResourceWithConnectionString, IResourceWithCustomWithReference<QdrantServerResource>
{
    internal const string PrimaryEndpointName = "grpc";
    internal const string HttpEndpointName = "http";

    /// <summary>
    /// Initializes a new instance of the <see cref="QdrantServerResource"/> class.
    /// </summary>
    /// <param name="name">The name of the resource.</param>
    /// <param name="apiKey">A <see cref="ParameterResource"/> that contains the API Key</param>
    public QdrantServerResource(string name, ParameterResource apiKey) : base(name)
    {
        ArgumentNullException.ThrowIfNull(apiKey);
        ApiKeyParameter = apiKey;
    }

    private EndpointReference? _primaryEndpoint;
    private EndpointReference? _httpEndpoint;

    /// <summary>
    /// Gets the parameter that contains the Qdrant API key.
    /// </summary>
    public ParameterResource ApiKeyParameter { get; }

    /// <summary>
    /// Gets the gRPC endpoint for the Qdrant database.
    /// </summary>
    public EndpointReference PrimaryEndpoint => _primaryEndpoint ??= new(this, PrimaryEndpointName);

    /// <summary>
    /// Gets the host endpoint reference for the gRPC endpoint.
    /// </summary>
    public EndpointReferenceExpression GrpcHost => PrimaryEndpoint.Property(EndpointProperty.Host);

    /// <summary>
    /// Gets the port endpoint reference for the gRPC endpoint.
    /// </summary>
    public EndpointReferenceExpression GrpcPort => PrimaryEndpoint.Property(EndpointProperty.Port);

    /// <summary>
    /// Gets the HTTP endpoint for the Qdrant database.
    /// </summary>
    public EndpointReference HttpEndpoint => _httpEndpoint ??= new(this, HttpEndpointName);

    /// <summary>
    /// Gets the host endpoint reference for the HTTP endpoint.
    /// </summary>
    public EndpointReferenceExpression HttpHost => HttpEndpoint.Property(EndpointProperty.Host);

    /// <summary>
    /// Gets the port endpoint reference for the HTTP endpoint.
    /// </summary>
    public EndpointReferenceExpression HttpPort => HttpEndpoint.Property(EndpointProperty.Port);

    /// <summary>
    /// Gets the connection string expression for the Qdrant gRPC endpoint.
    /// </summary>
    public ReferenceExpression ConnectionStringExpression =>
       ReferenceExpression.Create(
            $"Endpoint={PrimaryEndpoint.Property(EndpointProperty.Url)};Key={ApiKeyParameter}");

    /// <summary>
    /// Gets the connection URI expression for the Qdrant gRPC endpoint.
    /// </summary>
    /// <remarks>
    /// Format: <c>http://{host}:{port}</c>.
    /// </remarks>
    public ReferenceExpression UriExpression => ReferenceExpression.Create($"{PrimaryEndpoint.Property(EndpointProperty.Url)}");

    /// <summary>
    /// Gets the connection string expression for the Qdrant HTTP endpoint.
    /// </summary>
    public ReferenceExpression HttpConnectionStringExpression =>
        ReferenceExpression.Create(
            $"Endpoint={HttpEndpoint.Property(EndpointProperty.Url)};Key={ApiKeyParameter}");

    /// <summary>
    /// Gets the connection URI expression for the Qdrant HTTP endpoint.
    /// </summary>
    /// <remarks>
    /// Format: <c>http://{host}:{port}</c>. The scheme reflects the endpoint configuration and may be <c>https</c> when TLS is enabled.
    /// </remarks>
    public ReferenceExpression HttpUriExpression => ReferenceExpression.Create($"{HttpEndpoint.Property(EndpointProperty.Url)}");

    IEnumerable<KeyValuePair<string, ReferenceExpression>> IResourceWithConnectionString.GetConnectionProperties()
    {
        yield return new("GrpcHost", ReferenceExpression.Create($"{GrpcHost}"));
        yield return new("GrpcPort", ReferenceExpression.Create($"{GrpcPort}"));
        yield return new("HttpHost", ReferenceExpression.Create($"{HttpHost}"));
        yield return new("HttpPort", ReferenceExpression.Create($"{HttpPort}"));
        yield return new("ApiKey", ReferenceExpression.Create($"{ApiKeyParameter}"));
        yield return new("Uri", UriExpression);
        yield return new("HttpUri", HttpUriExpression);
    }

    static IResourceBuilder<TDestination>? IResourceWithCustomWithReference<QdrantServerResource>.TryWithReference<TDestination>(
        IResourceBuilder<TDestination> builder,
        IResourceBuilder<IResource> source,
        string? connectionName,
        bool optional,
        string? name)
    {
        if (source is not IResourceBuilder<QdrantServerResource> qdrantSource)
        {
            return null;
        }

        if (optional)
        {
            throw new InvalidOperationException("Optional references are not supported for Qdrant resources.");
        }

        if (name is not null)
        {
            throw new InvalidOperationException("Named service references are not supported for Qdrant resources.");
        }

        return QdrantBuilderExtensions.WithReference(builder, qdrantSource, connectionName);
    }
}
