// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using Azure.Core;
using Azure.Identity;
using Npgsql;

namespace Aspire;

internal static class ManagedIdentityTokenCredentialHelpers
{
    private const string AzureDatabaseForPostgresSqlScope = "https://ossrdbms-aad.database.windows.net/.default";
    private const string AzureManagementScope = "https://management.azure.com/.default";

    private static readonly TokenRequestContext s_databaseForPostgresSqlTokenRequestContext = new([AzureDatabaseForPostgresSqlScope]);
    private static readonly TokenRequestContext s_managementTokenRequestContext = new([AzureManagementScope]);

    public static bool ConfigureEntraIdAuthentication(this NpgsqlDataSourceBuilder dataSourceBuilder, TokenCredential? credential)
    {
        credential ??= new DefaultAzureCredential();
        var configuredAuth = false;

        // The connection string requires the username to be provided. Since it will depend on the Managed Identity that is used
        // we attempt to get the username from the access token if it's not defined.

        if (string.IsNullOrEmpty(dataSourceBuilder.ConnectionStringBuilder.Username))
        {
            // Ensure to use the management scope, so the token contains user names for all managed identity types - e.g. user and service principal
            var token = credential.GetToken(s_managementTokenRequestContext, default);

            if (TryGetUsernameFromToken(token.Token, out var username))
            {
                dataSourceBuilder.ConnectionStringBuilder.Username = username;
                configuredAuth = true;
            }
            else
            {
                // Otherwise check using the PostgresSql scope
                token = credential.GetToken(s_databaseForPostgresSqlTokenRequestContext, default);

                if (TryGetUsernameFromToken(token.Token, out username))
                {
                    dataSourceBuilder.ConnectionStringBuilder.Username = username;
                    configuredAuth = true;
                }
            }

            // If we still don't have a username, we let Npgsql handle the error when trying to connect.
            // The user will be hinted to provide a username by using the configureDataSourceBuilder callback.
        }

        if (string.IsNullOrEmpty(dataSourceBuilder.ConnectionStringBuilder.Password))
        {
            // The token is not cached since it is refreshed for each new physical connection, or when it has expired.

            dataSourceBuilder.UsePasswordProvider(
                passwordProvider: _ => credential.GetToken(s_databaseForPostgresSqlTokenRequestContext, default).Token,
                passwordProviderAsync: async (_, ct) => (await credential.GetTokenAsync(s_databaseForPostgresSqlTokenRequestContext, default).ConfigureAwait(false)).Token
            );

            configuredAuth = true;
        }

        return configuredAuth;
    }

    private static bool TryGetUsernameFromToken(string jwtToken, out string? username)
    {
        username = null;

        try
        {
            // Split the token into its parts (Header, Payload, Signature)
            var tokenParts = jwtToken.Split('.');
            if (tokenParts.Length != 3)
            {
                return false;
            }

            // The payload is the second part, Base64Url encoded
            var payload = tokenParts[1];

            // Add padding if necessary
            payload = AddBase64Padding(payload);

            // Decode the payload from Base64Url
            var decodedBytes = Convert.FromBase64String(payload);

            // Parse the decoded payload as JSON
            var reader = new Utf8JsonReader(decodedBytes);
            var payloadJson = JsonElement.ParseValue(ref reader);

            // Try to get the username from 'xms_mirid', 'upn', 'preferred_username', or 'unique_name' claims
            if (payloadJson.TryGetProperty("xms_mirid", out var xms_mirid) &&
                xms_mirid.GetString() is string xms_miridString &&
                ParsePrincipalName(xms_miridString) is string principalName)
            {
                username = principalName;
            }
            else if (payloadJson.TryGetProperty("upn", out var upn))
            {
                username = upn.GetString();
            }
            else if (payloadJson.TryGetProperty("preferred_username", out var preferredUsername))
            {
                username = preferredUsername.GetString();
            }
            else if (payloadJson.TryGetProperty("unique_name", out var uniqueName))
            {
                username = uniqueName.GetString();
            }

            return username != null;
        }
        catch
        {
            return false;
        }
    }

    // parse the xms_mirid claim which look like
    // /subscriptions/{subId}/resourcegroups/{resourceGroup}/providers/Microsoft.ManagedIdentity/userAssignedIdentities/{principalName}
    private static string? ParsePrincipalName(string xms_mirid)
    {
        var lastSlashIndex = xms_mirid.LastIndexOf('/');
        if (lastSlashIndex == -1)
        {
            return null;
        }

        var beginning = xms_mirid.AsSpan(0, lastSlashIndex);
        var principalName = xms_mirid.AsSpan(lastSlashIndex + 1);

        if (principalName.IsEmpty || !beginning.EndsWith("providers/Microsoft.ManagedIdentity/userAssignedIdentities", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return principalName.ToString();
    }

    private static string AddBase64Padding(string base64) => (base64.Length % 4) switch
    {
        2 => base64 + "==",
        3 => base64 + "=",
        _ => base64,
    };
}
