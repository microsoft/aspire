// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.IO.Hashing;
using System.Text;
using Azure.Provisioning;

namespace Aspire.Hosting.Azure.ConnectorNamespace.Provisioning;

internal static class ConnectorNamespaceBicepIdentifiers
{
    public const string Gateway = "connectorGateway";

    private const int ReadablePartLength = 16;
    private const int GatewayResourceNameReadablePartLength = 11;

    public static string CreateGatewayResourceNamePrefix(string connectorNamespaceName)
    {
        var prefix = new StringBuilder(connectorNamespaceName.Length);
        foreach (var character in connectorNamespaceName)
        {
            if (char.IsAsciiLetter(character))
            {
                prefix.Append(char.ToLowerInvariant(character));
            }
        }

        // Bicep's uniqueString function returns 13 characters. Limit the readable portion first so
        // the uniqueness suffix is always retained within the Connector Namespace 24-character limit.
        return prefix.Length <= GatewayResourceNameReadablePartLength
            ? prefix.ToString()
            : prefix.ToString(0, GatewayResourceNameReadablePartLength);
    }

    public static string CreateConnection(string connectorNamespaceName, string connectionName)
        => Create("connectorConnection", connectorNamespaceName, connectionName);

    public static string CreateMcpServerConfig(string connectorNamespaceName, string configName)
        => Create("connectorMcpServer", connectorNamespaceName, configName);

    public static string CreateAccessPolicy(string connectorNamespaceName, string connectionName, string policyName)
        => Create("connectorAccessPolicy", connectorNamespaceName, connectionName, policyName);

    public static string CreateMcpAccessPolicy(string connectorNamespaceName, string configName, string policyName)
        => Create("connectorMcpAccessPolicy", connectorNamespaceName, configName, policyName);

    private static string Create(string prefix, params string[] names)
    {
        // Prefixing keeps child declarations away from module parameters and outputs, while the
        // delimited full-name hash prevents distinct readable prefixes from collapsing to one symbol.
        var identity = $"{prefix}\0{string.Join('\0', names)}";
        var hash = XxHash3.HashToUInt64(Encoding.UTF8.GetBytes(identity))
            .ToString("x16", CultureInfo.InvariantCulture);
        var readableNames = names.Select(static name =>
        {
            var normalizedName = Infrastructure.NormalizeBicepIdentifier(name);
            return normalizedName.Length <= ReadablePartLength
                ? normalizedName
                : normalizedName[..ReadablePartLength];
        });

        return $"{prefix}_{string.Join('_', readableNames)}_{hash}";
    }
}
