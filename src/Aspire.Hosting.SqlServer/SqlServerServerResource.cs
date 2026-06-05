// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// A resource that represents a SQL Server container.
/// </summary>
public class SqlServerServerResource : ContainerResource, IResourceWithConnectionString
{
    internal const string PrimaryEndpointName = "tcp";
    private const string DefaultUserName = "sa";

    /// <summary>
    /// Initializes a new instance of the <see cref="SqlServerServerResource"/> class.
    /// </summary>
    /// <param name="name">The name of the resource.</param>
    /// <param name="password">A parameter that contains the SQL Server password.</param>
    public SqlServerServerResource(string name, ParameterResource password) : base(name)
    {
        ArgumentNullException.ThrowIfNull(password);

        PrimaryEndpoint = new(this, PrimaryEndpointName);
        PasswordParameter = password;
    }

    /// <summary>
    /// Gets the primary endpoint for the SQL Server.
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
    /// Gets the parameter that contains the SQL Server password.
    /// </summary>
    public ParameterResource PasswordParameter { get; private set; }

    private ReferenceExpression ConnectionString =>
        ReferenceExpression.Create(
            $"Server={PrimaryEndpoint.Property(EndpointProperty.IPV4Host)},{PrimaryEndpoint.Property(EndpointProperty.Port)};User ID={DefaultUserName};Password={PasswordParameter};TrustServerCertificate=true");

    /// <summary>
    /// Gets a reference to the user name for the SQL Server.
    /// </summary>
    /// <remarks>
    /// Returns the user name parameter if specified, otherwise returns the default user name "sa".
    /// </remarks>
    public ReferenceExpression UserNameReference => ReferenceExpression.Create($"{DefaultUserName}");

    /// <summary>
    /// Gets the connection URI expression for the SQL Server.
    /// </summary>
    /// <remarks>
    /// Format: <c>mssql://{Username}:{Password}@{Host}:{Port}</c>.
    /// </remarks>
    public ReferenceExpression UriExpression =>
        ReferenceExpression.Create($"mssql://{DefaultUserName:uri}:{PasswordParameter:uri}@{Host}:{Port}");

    internal ReferenceExpression BuildJdbcConnectionString(string? databaseName = null)
    {
        var builder = new ReferenceExpressionBuilder();
        builder.AppendLiteral("jdbc:sqlserver://");
        builder.Append($"{Host}");
        builder.AppendLiteral(":");
        builder.Append($"{Port}");

        if (!string.IsNullOrEmpty(databaseName))
        {
            var databaseNameReference = ReferenceExpression.Create($"{databaseName:uri}");
            builder.AppendLiteral(";databaseName=");
            builder.Append($"{databaseNameReference}");
        }

        builder.AppendLiteral(";trustServerCertificate=true");

        return builder.Build();
    }

    /// <summary>
    /// Gets the JDBC connection string for the SQL Server.
    /// </summary>
    /// <remarks>
    /// <para>Format: <c>jdbc:sqlserver://{host}:{port};trustServerCertificate=true</c>.</para>
    /// <para>User and password credentials are not included in the JDBC connection string. Use the <c>Username</c> and <c>Password</c> connection properties to access credentials.</para>
    /// </remarks>
    public ReferenceExpression JdbcConnectionString => BuildJdbcConnectionString();

    /// <summary>
    /// Gets the connection string expression for the SQL Server.
    /// </summary>
    public ReferenceExpression ConnectionStringExpression
    {
        get
        {
            if (this.TryGetLastAnnotation<ConnectionStringRedirectAnnotation>(out var connectionStringAnnotation))
            {
                return connectionStringAnnotation.Resource.ConnectionStringExpression;
            }

            return ConnectionString;
        }
    }

    /// <summary>
    /// Gets the connection string for the SQL Server.
    /// </summary>
    /// <param name="cancellationToken"> A <see cref="CancellationToken"/> to observe while waiting for the task to complete.</param>
    /// <returns>A connection string for the SQL Server in the form "Server=host,port;User ID=sa;Password=password;TrustServerCertificate=true".</returns>
    public ValueTask<string?> GetConnectionStringAsync(CancellationToken cancellationToken = default)
    {
        if (this.TryGetLastAnnotation<ConnectionStringRedirectAnnotation>(out var connectionStringAnnotation))
        {
            return connectionStringAnnotation.Resource.GetConnectionStringAsync(cancellationToken);
        }

        return ConnectionString.GetValueAsync(cancellationToken);
    }

    private readonly Dictionary<string, string> _databases = new(StringComparers.ResourceName);
    private readonly List<SqlServerDatabaseResource> _databaseResources = [];

    /// <summary>
    /// A dictionary where the key is the resource name and the value is the database name.
    /// </summary>
    public IReadOnlyDictionary<string, string> Databases => _databases;

    internal void SetPassword(ParameterResource password)
    {
        ArgumentNullException.ThrowIfNull(password);

        PasswordParameter = password;
    }

    internal void AddDatabase(SqlServerDatabaseResource database)
    {
        _databases.TryAdd(database.Name, database.DatabaseName);
        _databaseResources.Add(database);
    }

    internal IReadOnlyList<SqlServerDatabaseResource> DatabaseResources => _databaseResources;

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
