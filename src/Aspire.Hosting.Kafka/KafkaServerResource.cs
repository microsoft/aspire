// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting;

/// <summary>
/// A resource that represents a Kafka broker.
/// </summary>
/// <param name="name">The name of the resource.</param>
[AspireExport(ExposeProperties = true)]
public class KafkaServerResource(string name) : ContainerResource(name), IResourceWithConnectionString, IResourceWithEnvironment
{
    // This endpoint is used for host processes Kafka broker communication.
    internal const string PrimaryEndpointName = "tcp";
    // This endpoint is used for container to broker communication.
    internal const string InternalEndpointName = "internal";
    internal const string DefaultUserName = "kafka";

    private EndpointReference? _primaryEndpoint;
    private EndpointReference? _internalEndpoint;

    /// <summary>
    /// Initializes a new instance of the <see cref="KafkaServerResource"/> class.
    /// </summary>
    /// <param name="name">The name of the resource.</param>
    /// <param name="userName">A parameter that contains the SASL user name, or <see langword="null"/> to use a default value.</param>
    /// <param name="password">A parameter that contains the SASL password, or <see langword="null"/> to disable authentication.</param>
    public KafkaServerResource(string name, ParameterResource? userName, ParameterResource? password) : this(name)
    {
        UserNameParameter = userName;
        PasswordParameter = password;
    }

    /// <summary>
    /// Gets the primary endpoint for the Kafka broker. This endpoint is used for host processes to Kafka broker communication.
    /// To connect to the Kafka broker from a container, use <see cref="InternalEndpoint"/>.
    /// </summary>
    public EndpointReference PrimaryEndpoint => _primaryEndpoint ??= new(this, PrimaryEndpointName, KnownNetworkIdentifiers.LocalhostNetwork);

    /// <summary>
    /// Gets the host endpoint reference for the primary endpoint.
    /// </summary>
    public EndpointReferenceExpression Host => PrimaryEndpoint.Property(EndpointProperty.Host);

    /// <summary>
    /// Gets the port endpoint reference for the primary endpoint.
    /// </summary>
    public EndpointReferenceExpression Port => PrimaryEndpoint.Property(EndpointProperty.Port);

    /// <summary>
    /// Gets the internal endpoint for the Kafka broker. This endpoint is used for container to broker communication.
    /// To connect to the Kafka broker from a host process, use <see cref="PrimaryEndpoint"/>.
    /// </summary>
    public EndpointReference InternalEndpoint => _internalEndpoint ??= new(this, InternalEndpointName, KnownNetworkIdentifiers.DefaultAspireContainerNetwork);

    /// <summary>
    /// Gets or sets the parameter that contains the SASL user name for the Kafka broker.
    /// </summary>
    public ParameterResource? UserNameParameter { get; set; }

    /// <summary>
    /// Gets a reference to the SASL user name for the Kafka broker.
    /// </summary>
    /// <remarks>
    /// Returns the user name parameter if specified, otherwise returns the default user name "kafka".
    /// </remarks>
    public ReferenceExpression UserNameReference =>
        UserNameParameter is not null ?
            ReferenceExpression.Create($"{UserNameParameter}") :
            ReferenceExpression.Create($"{DefaultUserName}");

    /// <summary>
    /// Gets or sets the parameter that contains the SASL password for the Kafka broker.
    /// </summary>
    /// <remarks>
    /// When <see langword="null"/> the broker listens without authentication.
    /// </remarks>
    public ParameterResource? PasswordParameter { get; set; }

    /// <summary>
    /// Gets the connection string expression for the Kafka broker.
    /// </summary>
    /// <remarks>
    /// When no password is configured the connection string is the bare <c>{host}:{port}</c> of the broker.
    /// When a password is configured it is a semicolon separated list of Confluent client configuration
    /// properties: <c>BootstrapServers={host}:{port};SecurityProtocol=SaslPlaintext;SaslMechanism=Plain;SaslUsername={user};SaslPassword="{password}"</c>.
    /// The password is quoted so that it may contain <c>;</c> and <c>=</c>; it must not contain a double quote.
    /// </remarks>
    public ReferenceExpression ConnectionStringExpression => BuildConnectionString();

    private ReferenceExpression BuildConnectionString()
    {
        if (PasswordParameter is null)
        {
            return ReferenceExpression.Create($"{PrimaryEndpoint.Property(EndpointProperty.HostAndPort)}");
        }

        var builder = new ReferenceExpressionBuilder();
        builder.Append($"BootstrapServers={PrimaryEndpoint.Property(EndpointProperty.HostAndPort)}");
        builder.AppendLiteral(";SecurityProtocol=SaslPlaintext;SaslMechanism=Plain;SaslUsername=");
        builder.Append($"{UserNameReference}");
        builder.Append($";SaslPassword=\"{PasswordParameter}\"");

        return builder.Build();
    }

    IEnumerable<KeyValuePair<string, ReferenceExpression>> IResourceWithConnectionString.GetConnectionProperties()
    {
        yield return new("Host", ReferenceExpression.Create($"{Host}"));
        yield return new("Port", ReferenceExpression.Create($"{Port}"));

        if (PasswordParameter is not null)
        {
            yield return new("Username", UserNameReference);
            yield return new("Password", ReferenceExpression.Create($"{PasswordParameter}"));
        }
    }
}
