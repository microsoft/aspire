// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using Azure.Core;

namespace Aspire.Hosting.Azure.Provisioning.Internal;

/// <summary>
/// Default implementation of <see cref="IAzurePrincipalProvider"/>.
/// </summary>
internal sealed class DefaultAzurePrincipalProvider(ITokenCredentialProvider tokenCredentialProvider) : IAzurePrincipalProvider
{
    // Microsoft Entra principal-type values surfaced via the `idtyp` claim on access tokens.
    // See: https://learn.microsoft.com/en-us/entra/identity-platform/access-token-claims-reference#payload-claims
    private const string IdTypUser = "user";
    private const string IdTypApp = "app";

    // Values accepted by the `principalType` property on Microsoft.Authorization/roleAssignments.
    // We don't emit "Group" here because access tokens never represent a group identity directly.
    private const string PrincipalTypeUser = "User";
    private const string PrincipalTypeServicePrincipal = "ServicePrincipal";

    public async Task<AzurePrincipal> GetPrincipalAsync(CancellationToken cancellationToken = default)
    {
        var credential = tokenCredentialProvider.TokenCredential;
        var response = await credential.GetTokenAsync(new(["https://graph.windows.net/.default"]), cancellationToken).ConfigureAwait(false);

        static AzurePrincipal ParseToken(in AccessToken response)
        {
            // Decode the JWT payload (the middle segment of "header.payload.signature"). JWTs use
            // base64url with stripped padding, so swap the URL-safe characters back and re-pad
            // to a length divisible by four before base64-decoding. Example payload shape:
            //   { "oid":"<guid>","upn":"user@contoso.com","idtyp":"user","iss":"..." }
            // For app-only (service principal) tokens the `upn` claim is absent and `idtyp` is "app".
            var oid = string.Empty;
            var upn = string.Empty;
            // Default to "User" so older tokens (and any flow that omits `idtyp`) keep the
            // historical behavior — a hardcoded "User" principalType — instead of regressing
            // to an empty value.
            var principalType = PrincipalTypeUser;
            // A JWT is "header.payload.signature". The token credential should always return
            // that shape, but guard explicitly so a malformed token surfaces as a clear error
            // instead of a confusing IndexOutOfRangeException deep in the parser.
            var parts = response.Token.Split('.');
            if (parts.Length < 3)
            {
                throw new InvalidOperationException(
                    $"The access token returned by the credential is not a valid JWT (expected 3 '.'-separated segments, found {parts.Length}).");
            }
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
                        upn = reader.GetString()!;
                    }
                    else if (header == "idtyp")
                    {
                        reader.Read();
                        // `idtyp` values are lower-case per Entra spec, but compare case-insensitively
                        // for resilience against future producers that may emit different casing.
                        var idtyp = reader.GetString();
                        if (string.Equals(idtyp, IdTypApp, StringComparison.OrdinalIgnoreCase))
                        {
                            principalType = PrincipalTypeServicePrincipal;
                        }
                        else if (string.Equals(idtyp, IdTypUser, StringComparison.OrdinalIgnoreCase))
                        {
                            principalType = PrincipalTypeUser;
                        }
                    }
                    else
                    {
                        reader.Read();
                    }
                }
            }
            return new AzurePrincipal(Guid.Parse(oid), upn, principalType);
        }

        return ParseToken(response);
    }
}