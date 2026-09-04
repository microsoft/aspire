// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
using System.Diagnostics.CodeAnalysis;

#pragma warning disable ASPIREMONGODB001

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
    [Experimental("ASPIREMONGODB001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    public string? ReplicaSetName { get; internal set; }

    /// <summary>
    /// Gets a value indicating whether <see cref="PasswordParameter"/> was generated for this resource rather than supplied
    /// by the caller. A generated password may be replaced by a shared one (for example when the server joins a replica
    /// set); one the caller chose may not.
    /// </summary>
    internal bool PasswordParameterWasGenerated { get; set; }

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
    /// <remarks>
    /// This property proxies through to <see cref="EndpointAnnotation.TlsEnabled"/> on the <see cref="PrimaryEndpoint"/>,
    /// which is turned on when an HTTPS/TLS certificate is determined to be available for the resource. It is resolved
    /// lazily so that it stays correct even when TLS is enabled later in the application lifecycle (during
    /// <c>BeforeStartEvent</c>).
    /// </remarks>
    [Experimental("ASPIREMONGODB001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    public bool TlsEnabled => PrimaryEndpoint.TlsEnabled;

    /// <summary>
    /// Gets the TLS mode the MongoDB server is started in when TLS is active.
    /// </summary>
    internal MongoDBTlsMode TlsMode =>
        this.TryGetLastAnnotation<MongoDBServerTlsModeAnnotation>(out var annotation)
            ? annotation.Mode
            : MongoDBTlsMode.RequireTls;

    /// <summary>
    /// Gets a value indicating whether the MongoDB server accepts TLS connections whose peer certificate cannot be validated.
    /// </summary>
    internal bool TlsAllowInvalidCertificates => this.HasAnnotationOfType<MongoDBServerTlsAllowInvalidCertificatesAnnotation>();

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
            // NOTE: The default read preference is "primary", which means that even though we have set `directConnection` to `true`, any read operation run against individual nodes (which is what this connection string should enable, including for complex healthchecks, for example) would be rejected by the server. The way to resolve that is to set the read preference to either `secondaryPreferred`, which we do here for that reason. See https://www.mongodb.com/docs/manual/core/read-preference-use-cases/#indications-to-use-non-primary-read-preference
            builder.AppendLiteral("&readPreference=secondaryPreferred");
        }

        // NOTE: TLS is turned on lazily (at `BeforeStartEvent` time, once a certificate is known to be available for this
        // resource), which is after this expression is normally built, so the flag has to be resolved lazily too.
        builder.Append($"{PrimaryEndpoint.GetTlsValue(
            enabledValue: PasswordParameter is not null || ReplicaSetName is not null
                ? ReferenceExpression.Create($"&tls=true")
                : ReferenceExpression.Create($"?tls=true"),
            disabledValue: ReferenceExpression.Empty)}");

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
