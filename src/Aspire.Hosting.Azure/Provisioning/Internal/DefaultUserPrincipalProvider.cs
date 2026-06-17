// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using Azure.Core;
using Azure.Provisioning.Authorization;

namespace Aspire.Hosting.Azure.Provisioning.Internal;

/// <summary>
/// Default implementation of <see cref="IUserPrincipalProvider"/>.
/// </summary>
internal sealed class DefaultUserPrincipalProvider(ITokenCredentialProvider tokenCredentialProvider) : IUserPrincipalProvider
{
    public async Task<UserPrincipal> GetUserPrincipalAsync(CancellationToken cancellationToken = default)
    {
        var credential = tokenCredentialProvider.TokenCredential;
        var response = await credential.GetTokenAsync(new(["https://graph.windows.net/.default"]), cancellationToken).ConfigureAwait(false);

        static UserPrincipal ParseToken(in AccessToken response)
        {
            // Parse Azure access-token payloads to get the object id and principal type.
            // User tokens typically include:
            //   { "oid": "<object-id>", "upn": "user@contoso.com" }
            // App-only tokens typically include:
            //   { "oid": "<service-principal-object-id>", "idtyp": "app", "appid": "<client-id>" }
            var oid = string.Empty;
            var principalName = string.Empty;
            var appId = string.Empty;
            var isAppPrincipal = false;
            var parts = response.Token.Split('.');
            var part = parts[1];
            var convertedToken = part.ToString().Replace('_', '/').Replace('-', '+');

            switch (part.Length % 4)
            {
                case 2:
                    convertedToken += "==";
                    break;
                case 3:
                    convertedToken += "=";
                    break;
            }
            var bytes = Convert.FromBase64String(convertedToken);
            Utf8JsonReader reader = new(bytes);
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.PropertyName)
                {
                    var header = reader.GetString();
                    if (header == "oid")
                    {
                        reader.Read();
                        oid = reader.GetString()!;
                    }
                    else if (header is "upn" or "email")
                    {
                        reader.Read();
                        principalName = reader.GetString()!;
                    }
                    else if (header == "appid")
                    {
                        reader.Read();
                        appId = reader.GetString()!;
                    }
                    else if (header == "idtyp")
                    {
                        reader.Read();
                        isAppPrincipal = string.Equals(reader.GetString(), "app", StringComparison.OrdinalIgnoreCase);
                    }
                    else
                    {
                        reader.Read();
                    }
                }
            }

            var hasAppId = !string.IsNullOrEmpty(appId);
            var principalType = isAppPrincipal || (string.IsNullOrEmpty(principalName) && hasAppId)
                ? RoleManagementPrincipalType.ServicePrincipal
                : RoleManagementPrincipalType.User;

            if (string.IsNullOrEmpty(principalName) && hasAppId)
            {
                principalName = appId;
            }

            return new UserPrincipal(Guid.Parse(oid), principalName, principalType);
        }

        return ParseToken(response);
    }
}