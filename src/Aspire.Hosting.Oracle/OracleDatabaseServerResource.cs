// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// A resource that represents an Oracle Database container.
/// </summary>
[AspireExport(ExposeProperties = true)]
public class OracleDatabaseServerResource : ContainerResource, IResourceWithConnectionString
{
    internal const string PrimaryEndpointName = "tcp";
    private const string DefaultUserName = "system";

    /// <summary>
    /// Initializes a new instance of the <see cref="OracleDatabaseServerResource"/> class.
    /// </summary>
    /// <param name="name">The name of the resource.</param>
    /// <param name="password">A parameter that contains the Oracle Database server password.</param>
    public OracleDatabaseServerResource(string name, ParameterResource password) : base(name)
    {
        ArgumentNullException.ThrowIfNull(password);

        PrimaryEndpoint = new(this, PrimaryEndpointName);
        PasswordParameter = password;
    }

    /// <summary>
    /// Gets the primary endpoint for the Oracle server.
    /// </summary>
    public EndpointReference PrimaryEndpoint { get; }

    /// <summary>
    /// Gets the host endpoint reference for this resource.
    /// </summary>
    public EndpointReferenceExpression Host => PrimaryEndpoint.Property(EndpointProperty.Host);

    /// <summary>
    /// Gets the port endpoint reference for this resource.
    /// </summary>
    public EndpointReferenceExpression Port => PrimaryEndpoint.Property(EndpointProperty.Port);

    /// <summary>
    /// Gets the parameter that contains the Oracle Database server password.
    /// </summary>
    public ParameterResource PasswordParameter { get; }

    /// <summary>
    /// Gets the connection string expression for the Oracle Database server.
    /// </summary>
    public ReferenceExpression ConnectionStringExpression =>
        ReferenceExpression.Create(
            $"user id={DefaultUserName};password={PasswordParameter};data source={PrimaryEndpoint.Property(EndpointProperty.HostAndPort)}");

    /// <summary>
    /// Gets a reference to the user name for the Oracle server.
    /// </summary>
    /// <remarks>
    /// Returns the user name parameter if specified, otherwise returns the default user name "system".
    /// </remarks>
    public ReferenceExpression UserNameReference => ReferenceExpression.Create($"{DefaultUserName}");

    /// <summary>
    /// Gets the connection URI expression for the Oracle server.
    /// </summary>
    /// <remarks>
    /// Format: <c>oracle://{user}:{password}@{host}:{port}</c>.
    /// </remarks>
    public ReferenceExpression UriExpression => BuildUri();

    internal ReferenceExpression BuildUri(string? databaseName = null)
    {
        var builder = new ReferenceExpressionBuilder();
        builder.AppendLiteral("oracle://");
        builder.Append($"{DefaultUserName:uri}:{PasswordParameter:uri}@{Host}:{Port}");

        if (databaseName is not null)
        {
            builder.Append($"/{databaseName:uri}");
        }

        return builder.Build();
    }

    internal ReferenceExpression BuildJdbcConnectionString(string? databaseName = null)
    {
        var builder = new ReferenceExpressionBuilder();
        builder.AppendLiteral("jdbc:oracle:thin:@//");
        builder.Append($"{Host}");
        builder.AppendLiteral(":");
        builder.Append($"{Port}");

        if (!string.IsNullOrEmpty(databaseName))
        {
            builder.Append($"/{databaseName:uri}");
        }

        return builder.Build();
    }
    /// <summary>
    /// Gets the JDBC connection string for the Oracle Database server.
    /// </summary>
    /// <remarks>
    /// <para>Format: <c>jdbc:oracle:thin:@//{host}:{port}</c>.</para>
    /// <para>User and password credentials are not included in the JDBC connection string. Use the <c>Username</c> and <c>Password</c> connection properties to access credentials.</para>
    /// </remarks>
    public ReferenceExpression JdbcConnectionString => BuildJdbcConnectionString();

    private readonly Dictionary<string, string> _databases = new(StringComparers.ResourceName);

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
        yield return new("Password", ReferenceExpression.Create($"{PasswordParameter}"));
        yield return new("Uri", UriExpression);
        yield return new("JdbcConnectionString", JdbcConnectionString);
    }
}
