// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Data.Common;
using Confluent.Kafka;

namespace Aspire.Confluent.Kafka;

/// <summary>
/// Applies an Aspire supplied connection string onto a <see cref="ClientConfig"/>.
/// </summary>
/// <remarks>
/// A connection string is either a bare bootstrap server list, for example <c>localhost:9092</c>, or a semicolon
/// separated list of Confluent client configuration properties, for example
/// <c>BootstrapServers=localhost:9092;SecurityProtocol=SaslPlaintext;SaslMechanism=Plain;SaslUsername=kafka;SaslPassword="secret"</c>.
/// The latter is produced by <c>Aspire.Hosting.Kafka</c> when the broker is password protected.
/// </remarks>
internal static class KafkaConnectionString
{
    private const string BootstrapServersKey = "BootstrapServers";
    private const string SecurityProtocolKey = "SecurityProtocol";
    private const string SaslMechanismKey = "SaslMechanism";
    private const string SaslUsernameKey = "SaslUsername";
    private const string SaslPasswordKey = "SaslPassword";

    public static void Apply(string connectionString, ClientConfig config)
    {
        // A bare bootstrap server list contains no '=' and cannot be parsed as a keyed connection string.
        if (!connectionString.Contains('='))
        {
            config.BootstrapServers = connectionString;
            return;
        }

        var builder = new DbConnectionStringBuilder { ConnectionString = connectionString };

        if (GetValue(builder, BootstrapServersKey) is string bootstrapServers)
        {
            config.BootstrapServers = bootstrapServers;
        }

        if (GetValue(builder, SecurityProtocolKey) is string securityProtocol &&
            Enum.TryParse<SecurityProtocol>(securityProtocol, ignoreCase: true, out var parsedSecurityProtocol))
        {
            config.SecurityProtocol = parsedSecurityProtocol;
        }

        if (GetValue(builder, SaslMechanismKey) is string saslMechanism &&
            Enum.TryParse<SaslMechanism>(saslMechanism, ignoreCase: true, out var parsedSaslMechanism))
        {
            config.SaslMechanism = parsedSaslMechanism;
        }

        if (GetValue(builder, SaslUsernameKey) is string saslUsername)
        {
            config.SaslUsername = saslUsername;
        }

        if (GetValue(builder, SaslPasswordKey) is string saslPassword)
        {
            config.SaslPassword = saslPassword;
        }
    }

    private static string? GetValue(DbConnectionStringBuilder builder, string key)
        => builder.TryGetValue(key, out var value) ? value as string : null;
}
