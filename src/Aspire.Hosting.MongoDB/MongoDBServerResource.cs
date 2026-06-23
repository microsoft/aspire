// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// A resource that represents a MongoDB container.
/// </summary>
/// <param name="name">The name of the resource.</param>
[AspireExport(ExposeProperties = true)]
public class MongoDBServerResource(string name) : ContainerResource(name), IResourceWithConnectionString
{
    internal const string PrimaryEndpointName = "tcp";
    internal const string DefaultUserName = "admin";
    internal const string DefaultAuthenticationDatabase = "admin";
    internal const string DefaultAuthenticationMechanism = "SCRAM-SHA-256";

    private EndpointReference? _primaryEndpoint;

    /// <summary>
    /// Initialize a resource that represents a MongoDB container.
    /// </summary>
    /// <param name="name">The name of the resource.</param>
    /// <param name="userNameParameter">A parameter that contains the MongoDb server user name, or <see langword="null"/> to use a default value.</param>
    /// <param name="passwordParameter">A parameter that contains the MongoDb server password.</param>
    public MongoDBServerResource(string name, ParameterResource? userNameParameter, ParameterResource? passwordParameter) : this(name)
    {
        UserNameParameter = userNameParameter;
        PasswordParameter = passwordParameter;
    }

    /// <summary>
    /// Gets the primary endpoint for the MongoDB server.
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
    /// Gets the parameter that contains the MongoDb server password.
    /// </summary>
    public ParameterResource? PasswordParameter { get; internal set; }

    /// <summary>
    /// Gets the parameter that contains the MongoDb server username.
    /// </summary>
    public ParameterResource? UserNameParameter { get; internal set; }

    /// <summary>
    /// Gets the name of the replica set this MongoDB server belongs to, or <see langword="null"/> if it is not part of a replica set.
    /// </summary>
    public string? ReplicaSetName { get; internal set; }

    /// <summary>
    /// Gets a reference to the user name for the MongoDB server.
    /// </summary>
    /// <remarks>
    /// Returns the user name parameter if specified, otherwise returns the default user name "admin".
    /// </remarks>
    public ReferenceExpression UserNameReference =>
        UserNameParameter is not null ?
            ReferenceExpression.Create($"{UserNameParameter}") :
            ReferenceExpression.Create($"{DefaultUserName}");

    /// <summary>
    /// Gets the connection string for the MongoDB server.
    /// </summary>
    public ReferenceExpression ConnectionStringExpression => BuildConnectionString();

    /// <summary>
    /// Gets the connection URI expression for the MongoDB server.
    /// </summary>
    /// <remarks>
    /// Format: <c>mongodb://[user:password@]{host}:{port}[?authSource=admin&amp;authMechanism=SCRAM-SHA-256]</c>. The credential and query segments are included only when a password is configured.
    /// </remarks>
    public ReferenceExpression UriExpression => BuildConnectionString();

    /// <summary>
    /// Gets a value indicating whether TLS is enabled for the MongoDB server.
    /// </summary>
    public bool TlsEnabled => this.HasAnnotationOfType<MongoDBServerTlsAnnotation>();

    private static ReferenceExpression AuthenticationDatabaseReference => ReferenceExpression.Create($"{DefaultAuthenticationDatabase}");

    private static ReferenceExpression AuthenticationMechanismReference => ReferenceExpression.Create($"{DefaultAuthenticationMechanism}");

    internal ReferenceExpression BuildConnectionString(string? databaseName = null)
    {
        var builder = new ReferenceExpressionBuilder();
        builder.AppendLiteral("mongodb://");

        if (PasswordParameter is not null)
        {
            if (UserNameParameter is not null)
            {
                builder.Append($"{UserNameParameter:uri}:{PasswordParameter:uri}@");
            }
            else
            {
                builder.Append($"{DefaultUserName:uri}:{PasswordParameter:uri}@");
            }
        }

        builder.Append($"{PrimaryEndpoint.Property(EndpointProperty.HostAndPort)}");

        if (databaseName is not null || PasswordParameter is not null)
        {
            builder.AppendLiteral("/");
        }

        if (databaseName is not null)
        {
            builder.Append($"{databaseName:uri}");
        }

        if (PasswordParameter is not null)
        {
            builder.AppendLiteral("?authSource=");
            builder.Append($"{DefaultAuthenticationDatabase:uri}");
            builder.AppendLiteral("&authMechanism=");
            builder.Append($"{DefaultAuthenticationMechanism:uri}");
        }

        if (ReplicaSetName is not null)
        {
            builder.AppendLiteral(PasswordParameter is not null ? "&" : "?");
            // NOTE: This is necessary when connecting to a single node that happens to be part of the replica set. Otherwise, the driver will attempt to discover other nodes in the replica set, and this would most notably fail upon attempting to `rs.initialize` since the replica set is not fully initialized at that point.
            builder.AppendLiteral("directConnection=true");
        }

        if (TlsEnabled)
        {
            builder.AppendLiteral(PasswordParameter is not null || ReplicaSetName is not null ? "&" : "?");
            builder.AppendLiteral("tls=true");
        }

        return builder.Build();
    }

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
        yield return new("Username", UserNameReference);

        if (PasswordParameter is not null)
        {
            yield return new("Password", ReferenceExpression.Create($"{PasswordParameter}"));
            yield return new("AuthenticationDatabase", AuthenticationDatabaseReference);
            yield return new("AuthenticationMechanism", AuthenticationMechanismReference);
        }

        yield return new("Uri", UriExpression);
    }
}
