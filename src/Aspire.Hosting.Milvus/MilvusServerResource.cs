    // Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Milvus;

/// <summary>
/// A resource that represents a Milvus database.
/// </summary>
[AspireExport(ExposeProperties = true)]
public class MilvusServerResource : ContainerResource, IResourceWithConnectionString
{
    internal const string PrimaryEndpointName = "grpc";

    /// <summary>
    /// Initializes a new instance of the <see cref="MilvusServerResource"/> class.
    /// </summary>
    /// <param name="name">The name of the resource.</param>
    /// <param name="apiKey">A <see cref="ParameterResource"/> that contains the authentication apiKey/token</param>
    public MilvusServerResource(string name, ParameterResource apiKey) : base(name)
    {
        ArgumentNullException.ThrowIfNull(apiKey);
        ApiKeyParameter = apiKey;
    }

    private EndpointReference? _primaryEndpoint;

    /// <summary>
    /// Gets the parameter that contains the Milvus API key.
    /// </summary>
    public ParameterResource ApiKeyParameter { get; }

    /// <summary>
    /// Gets the gRPC endpoint for the Milvus database.
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
    /// Gets a valid access token to access the Milvus instance.
    /// </summary>
    public ReferenceExpression Token => ReferenceExpression.Create($"root:{ApiKeyParameter}");

    /// <summary>
    /// Gets the connection string expression for the Milvus gRPC endpoint.
    /// </summary>
    /// <remarks>
    /// Format: <c>Endpoint={uri};Key={token}</c>.
    /// </remarks>
    public ReferenceExpression ConnectionStringExpression =>
       ReferenceExpression.Create(
            $"Endpoint={UriExpression};Key={Token}");

    /// <summary>
    /// Gets URI expression for the Milvus instance.
    /// </summary>
    /// <remarks>
    /// Format: <c>http://{host}:{port}</c>.
    /// </remarks>
    public ReferenceExpression UriExpression => ReferenceExpression.Create($"{PrimaryEndpoint.Property(EndpointProperty.Url)}");

    private readonly Dictionary<string, string> _databases = new Dictionary<string, string>(StringComparers.ResourceName);

    /// <summary>
    /// A dictionary where the key is the resource name and the value is the database name.
    /// </summary>
    public IReadOnlyDictionary<string, string> Databases => _databases;

    internal void AddDatabase(string name, string databaseName)
    {
        _databases.TryAdd(name, databaseName);
    }

    IEnumerable<KeyValuePair<string, ReferenceExpression>> IResourceWithConnectionString.GetConnectionProperties()
    {
        yield return new("Host", ReferenceExpression.Create($"{Host}"));
        yield return new("Port", ReferenceExpression.Create($"{Port}"));
        yield return new("Token", ReferenceExpression.Create($"{Token}"));
        yield return new("Uri", UriExpression);
    }
}
