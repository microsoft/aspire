import { createBuilder, EntraIdPermissionType, EntraIdSignInAudience, EntraIdWellKnownApi } from './.aspire/modules/aspire.mjs';

const builder = await createBuilder();

const tenantId = await builder.addParameter("entra-tenant");
const clientId = await builder.addParameter("entra-client");
const clientSecret = await builder.addParameter("entra-secret", { secret: true });
const certificatePassword = await builder.addParameter("certificate-password", { secret: true });
const redirectUris = await builder.addParameter("redirect-uris");
const redirectHost = await builder.addParameter("redirect-host");
const ownerId = await builder.addParameter("entra-owner");
const exampleTenant = "aaaabbbb-0000-cccc-1111-dddd2222eeee";
const exampleClient = "00001111-aaaa-2222-bbbb-3333cccc4444";

const container = await builder.addContainer("api", "mcr.microsoft.com/dotnet/samples:aspnetapp")
    .withHttpEndpoint({ name: "http", targetPort: 8080 });
const executable = await builder.addExecutable("worker", "dotnet", ".", ["--info"]);
const endpoint = await container.getEndpoint("http");
const identity = await builder.addAzureUserAssignedIdentity("identity");

// Existing registrations: configuration only, with literal or parameter IDs in any combination.
const existingEntra = await builder.addExistingEntraApplication("existing-entra", tenantId, exampleClient);
await builder.addExistingEntraApplication("existing-entra-literals", exampleTenant, exampleClient);
await builder.addExistingEntraApplication("existing-entra-parameters", tenantId, clientId);
await existingEntra.withInstance("https://login.microsoftonline.com/");
await existingEntra.withSignInAudience(EntraIdSignInAudience.AzureADMultipleOrgs);
await existingEntra.withAudience("api://example");
await existingEntra.withClientSecret(clientSecret);
await existingEntra.withFederatedManagedIdentityCredential(exampleClient, {
    tokenExchangeUrl: "api://AzureADTokenExchange",
    tokenExchangeAuthority: "https://login.microsoftonline.com/",
});
await existingEntra.withCertificateFromKeyVault("https://vault.vault.azure.net", "certificate");
await existingEntra.withCertificateThumbprint("CurrentUser/My", "thumbprint");
await existingEntra.withCertificateDistinguishedName("CurrentUser/My", "CN=certificate");
await existingEntra.withCertificateFromFile("/certs/client.pfx", { password: certificatePassword });
await existingEntra.withSignedAssertionFile({ filePath: "/tokens/assertion" });
await existingEntra.withSignedAssertionFile();
await existingEntra.withSendX5C();
await existingEntra.withClientCapability("cp1");
await existingEntra.withAllowWebApiToBeAuthorizedByACL();
await existingEntra.withExtraQueryParameter("domain_hint", "contoso.com");
await container.withReference(existingEntra, { connectionName: "AUTH" });
await executable.withReference(existingEntra);

const fluentExistingEntra = await builder.addEntraIdApplication("fluent-existing-entra");
await fluentExistingEntra.asExistingApplication(exampleTenant, clientId);

// An existing API registration is configuration only: its URI and scope IDs describe what the registration already defines.
const existingApi = await builder.addExistingEntraApplication("existing-api", exampleTenant, exampleClient);
await existingApi.withApplicationIdUri({ applicationIdUri: "api://contoso-orders" });
await existingApi.withScope("orders.read", "Read orders.", { id: "33334444-aaaa-5555-bbbb-6666cccc7777" });

// Registrations created by Aspire.
const createdEntra = await builder.addEntraIdApplication("created-entra");
await createdEntra.withOwners(exampleClient);
await createdEntra.withOwners(ownerId);
await createdEntra.withApplicationOwners(ownerId);
await createdEntra.withServicePrincipalOwners(exampleClient);
await createdEntra.withIdTokenIssuance();
await createdEntra.withAppRole("User", "User", "Can use the API.");
await createdEntra.withAppRole("Admin", "Administrator", "Can administer the API.", {
    allowedMemberTypes: ["User", "Application"],
    id: "11112222-aaaa-3333-bbbb-4444cccc5555",
    isEnabled: false,
});
await createdEntra.withRedirectUris("https://localhost/signin");
await createdEntra.withRedirectUris(redirectUris);
await createdEntra.withRedirectUris(redirectHost, { callbackPaths: ["/signin", "/signout"] });
await createdEntra.withRedirectUris(endpoint, { callbackPaths: ["/signin"] });
await container.withReference(createdEntra);
await createdEntra.publishWithFederatedManagedIdentityCredential();
await createdEntra.publishWithFederatedManagedIdentityCredential({ identity: container });
await createdEntra.publishWithFederatedManagedIdentityCredential({ identity });

// An API exposed by one registration and called by another.
const apiEntra = await builder.addEntraIdApplication("api-entra");
await apiEntra.withApplicationIdUri();
await apiEntra.withApplicationIdUri({ applicationIdUri: "api://contoso-api" });
await apiEntra.withScope("access_as_user", "Allows the app to call the API.");
await apiEntra.withScope("admin", "Allows the app to administer the API.", {
    displayName: "Administer",
    userConsentDisplayName: "Administer on your behalf",
    userConsentDescription: "Allows the app to administer the API for you.",
    requiresAdminConsent: true,
    isEnabled: false,
    id: "11112222-aaaa-3333-bbbb-4444cccc5555",
});
const scopeUri = await apiEntra.getScopeUri("access_as_user");
await container.withEnvironment("API_SCOPE", scopeUri);
const preAuthorizedEntra = await builder.addEntraIdApplication("pre-authorized-entra");
await apiEntra.withPreAuthorizedApplication(preAuthorizedEntra, ["access_as_user"]);

await createdEntra.withApiPermissions(apiEntra, ["access_as_user"]);
await createdEntra.withAdminConsent(apiEntra, ["access_as_user"]);
await createdEntra.withApiPermissions(existingApi, ["orders.read"]);
await createdEntra.withExternalApiPermissions(EntraIdWellKnownApi.MicrosoftGraph, [
    { name: "User.Read" },
    { name: "User.Read.All", type: EntraIdPermissionType.Application },
]);
await createdEntra.withExternalApiPermissions(
    {
        displayName: "Contoso API",
        applicationId: "99998888-aaaa-7777-bbbb-6666cccc5555",
        resourceUri: "api://contoso",
        baseUrl: "https://contoso.example",
    },
    [{ name: "items.read", id: "22223333-aaaa-4444-bbbb-5555cccc6666" }]);
await createdEntra.withExternalApiAdminConsent(EntraIdWellKnownApi.MicrosoftGraph, [
    { name: "User.Read" },
    { name: "User.Read.All", type: EntraIdPermissionType.Application },
]);
await executable.withEntraIdReference(createdEntra, { connectionName: "AUTH", downstreamApisPrefix: "Apis" });

await builder.build().run();
