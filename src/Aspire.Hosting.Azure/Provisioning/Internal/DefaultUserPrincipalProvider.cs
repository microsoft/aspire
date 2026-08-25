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
            //   { "oid": "<object-id>", "upn": "user@contoso.com", "scp": "User.Read" }
            // App-only tokens typically include:
            //   { "oid": "<service-principal-object-id>", "appid": "<client-id>", "roles": ["Application.Read.All"] }
            // The optional idtyp claim is authoritative when present; otherwise scp distinguishes
            // delegated tokens from app-only tokens carrying application roles.
            // See https://learn.microsoft.com/entra/identity-platform/access-token-claims-reference.
            var oid = string.Empty;
            var principalName = string.Empty;
            var appId = string.Empty;
            var identityType = string.Empty;
            var hasDelegatedScopes = false;
            var hasApplicationRoles = false;
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
                        identityType = reader.GetString() ?? string.Empty;
                    }
                    else if (header == "scp")
                    {
                        reader.Read();
                        hasDelegatedScopes = !string.IsNullOrWhiteSpace(reader.GetString());
                    }
                    else if (header == "roles")
                    {
                        reader.Read();
                        if (reader.TokenType == JsonTokenType.StartArray)
                        {
                            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                            {
                                hasApplicationRoles |= reader.TokenType == JsonTokenType.String &&
                                    !string.IsNullOrWhiteSpace(reader.GetString());
                            }
                        }
                    }
                    else
                    {
                        reader.Read();
                    }
                }
            }

            var hasAppId = !string.IsNullOrEmpty(appId);
            var isAppPrincipal =
                string.Equals(identityType, "app", StringComparison.OrdinalIgnoreCase) ||
                (string.IsNullOrEmpty(identityType) && !hasDelegatedScopes && hasApplicationRoles);
            var principalType = isAppPrincipal
                ? RoleManagementPrincipalType.ServicePrincipal
                : RoleManagementPrincipalType.User;

            if (isAppPrincipal && string.IsNullOrEmpty(principalName) && hasAppId)
            {
                principalName = appId;
            }

            return new UserPrincipal(Guid.Parse(oid), principalName, principalType);
        }

        return ParseToken(response);
    }
}