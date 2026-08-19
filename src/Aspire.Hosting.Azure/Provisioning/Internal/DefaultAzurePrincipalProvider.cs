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
    // Microsoft Entra reports the token's identity type in the `idtyp` claim: "app" for app-only
    // (service principal / managed identity / federated workload identity) tokens and "user" for
    // user-delegated ones. Only "app" needs matching here because every other value — including
    // "user" and the claim being absent entirely — falls through to the User default below.
    // See: https://learn.microsoft.com/en-us/entra/identity-platform/access-token-claims-reference#payload-claims
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
            // A JWT is "header.payload.signature". The token credential should always return
            // that shape, but guard explicitly so a malformed token surfaces as a clear error
            // instead of a confusing IndexOutOfRangeException deep in the parser.
            var parts = response.Token.Split('.');
            if (parts.Length < 3)
            {
                throw new InvalidOperationException(
                    $"The access token returned by the credential is not a valid JWT (expected 3 '.'-separated segments, found {parts.Length}).");
            }

            // Decode the JWT payload (the middle segment). JWTs use base64url with stripped
            // padding (RFC 7515 §2), so swap the URL-safe characters back and re-pad to a length
            // divisible by four before base64-decoding. Example payload shape:
            //   { "oid":"<guid>","upn":"user@contoso.com","idtyp":"user","iss":"..." }
            // For app-only (service principal) tokens the `upn` claim is absent and `idtyp` is "app".
            var part = parts[1];
            var convertedToken = part.Replace('_', '/').Replace('-', '+');

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

            // Read claims from the root object only. JWT claims are top-level by definition, but a
            // claim's *value* can itself be an object or array — Entra emits `_claim_sources` that
            // way for the groups-overage case, and RFC 8693 delegation tokens nest identity claims
            // under `act`. A streaming reader that walks every token would treat a nested "oid" or
            // "idtyp" as if it were a real claim, and last-write-wins would silently swap the
            // principal these values describe. That matters here because they become the
            // principalId/principalType of an ARM role assignment, so picking up the wrong one
            // would grant access to the wrong identity. Microsoft also documents that new claims
            // may be added without notice, so scope the lookup structurally rather than relying on
            // today's payloads happening to be flat.
            using var document = JsonDocument.Parse(bytes);
            var root = document.RootElement;

            var oid = GetRootString(root, "oid");
            if (!Guid.TryParse(oid, out var principalId))
            {
                throw new InvalidOperationException(
                    "Unable to determine the Azure identity to provision as: the access token returned by " +
                    "the credential does not contain a valid 'oid' (object id) claim.");
            }

            // `upn` is the user principal name; `email` is the fallback for accounts that don't
            // carry one (for example guests). Neither is present on app-only tokens, which is why
            // an empty name is tolerated here.
            var upn = GetRootString(root, "upn") ?? GetRootString(root, "email") ?? string.Empty;

            // Default to "User" so older tokens — and any flow that omits `idtyp` — keep the
            // historical behavior of a hardcoded "User" principalType instead of regressing to an
            // empty value. `idtyp` is an optional claim that Entra only emits for app-only tokens
            // unless the resource opts in via `include_user_token`, so absence is not evidence of
            // a user identity; it just means we can't tell and fall back to the previous default.
            // The comparison is case-insensitive for resilience against future producers that emit
            // different casing than the lower-case values Entra documents.
            var principalType = string.Equals(GetRootString(root, "idtyp"), IdTypApp, StringComparison.OrdinalIgnoreCase)
                ? PrincipalTypeServicePrincipal
                : PrincipalTypeUser;

            return new AzurePrincipal(principalId, upn, principalType);
        }

        return ParseToken(response);
    }

    // Reads a string claim from the root of the payload. Returns null when the claim is absent or
    // is not a JSON string, so a structurally unexpected value is treated as "not supplied" rather
    // than throwing out of the middle of provisioning.
    private static string? GetRootString(JsonElement root, string claimName) =>
        root.TryGetProperty(claimName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
