import { createBuilder, EntraIdSignInAudience } from './.aspire/modules/aspire.mjs';

const builder = await createBuilder();
const tenant = await builder.addParameter("tenant");
const client = await builder.addParameter("client");
const secret = await builder.addParameter("secret", { secret: true });
const owner = await builder.addParameter("owner");
const redirect = await builder.addParameter("redirect");
const host = await builder.addParameter("host");
const id = "00000000-0000-0000-0000-000000000001";

const existing = await builder.addExistingEntraApplication("existing", "contoso.onmicrosoft.com", id);
const parameterized = await builder.addExistingEntraApplicationFromParameters("parameterized", tenant, client);
await existing.withClientSecret(secret)
    .withInstance("https://login.microsoftonline.us/")
    .withSignInAudience(EntraIdSignInAudience.AzureADMultipleOrgs)
    .withFederatedManagedIdentityCredential(id)
    .withCertificateFromKeyVault("https://example.vault.azure.net", "certificate")
    .withCertificateThumbprint("CurrentUser/My", "0123456789ABCDEF")
    .withCertificateDistinguishedName("CurrentUser/My", "CN=example")
    .withClientCapability("cp1")
    .withAllowWebApiToBeAuthorizedByACL()
    .withSendX5C()
    .withExtraQueryParameter("domain_hint", "contoso.com")
    .withAudience("api://example");

await existing.withCredential(await builder.createEntraIdFileCertificateCredential("/certs/client.pfx", { password: secret }));
await existing.withCredential(await builder.createEntraIdSignedAssertionFileCredential());
await parameterized.withCredential(await builder.createEntraIdSignedAssertionFileCredential({ filePath: "/tokens/assertion" }));
await existing.withCredential(await builder.createEntraIdFederatedIdentityCredential(id, {
    tokenExchangeUrl: "api://AzureADTokenExchange",
    tokenExchangeAuthority: "https://login.microsoftonline.com/contoso.onmicrosoft.com"
}));

const web = await builder.addContainer("web", "nginx").withHttpEndpoint({ targetPort: 80 });
await web.withReference(existing);
await web.withEntraIdReference(parameterized, { connectionName: "Identity" });

const created = await builder.addEntraIdApplication("created");
await created.withRedirectUris(["https://localhost:7123/signin-oidc"])
    .withRedirectUrisFromParameters([redirect])
    .withRedirectUrisForEndpoint(web.getEndpoint("http"), ["/signin-oidc"])
    .withRedirectUrisForHost(host, "/signin-oidc", ["/signout-callback-oidc"])
    .withApplicationOwners([id])
    .withApplicationOwnersFromParameters([owner])
    .withServicePrincipalOwners([id])
    .withServicePrincipalOwnersFromParameters([owner])
    .withOwners([id])
    .withOwnersFromParameters([owner])
    .withIdTokenIssuance()
    .publishWithFederatedManagedIdentityCredential();
await web.withReference(created);
const targeted = await builder.addEntraIdApplication("targeted");
await web.withReference(targeted);
await targeted.publishWithFederatedManagedIdentityCredential({ identity: web });
const identity = await builder.addAzureUserAssignedIdentity("identity");
const shared = await builder.addEntraIdApplication("shared");
await shared.publishWithFederatedManagedIdentityCredential({ identity });
await web.withAzureUserAssignedIdentity(identity).withReference(shared);

await builder.addEntraIdApplication("converted").asExistingApplication("contoso.onmicrosoft.com", id);
await builder.addEntraIdApplication("converted-parameters").asExistingApplicationFromParameters(tenant, client);

// Exercise the live ATS calls without starting resources or provisioning cloud infrastructure.
if (process.env["ENTRA_ID_VALIDATE_MODEL_ONLY"] === "1") {
    console.log("Entra ID model configured without provisioning.");
    process.exit(0);
}

await builder.build().run();
