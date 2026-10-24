import aspire.*;

void main() throws Exception {
    var builder = DistributedApplication.CreateBuilder();
    var tenant = builder.addParameter("tenant");
    var client = builder.addParameter("client");
    var secret = builder.addParameter("secret", new AddParameterOptions().secret(true));
    var owner = builder.addParameter("owner");
    var redirect = builder.addParameter("redirect");
    var host = builder.addParameter("host");
    var id = "00000000-0000-0000-0000-000000000001";

    var existing = builder.addExistingEntraApplication("existing", "contoso.onmicrosoft.com", id);
    var parameterized = builder.addExistingEntraApplicationFromParameters("parameterized", tenant, client);
    existing.withClientSecret(secret)
        .withInstance("https://login.microsoftonline.us/")
        .withSignInAudience(EntraIdSignInAudience.AZURE_ADMULTIPLE_ORGS)
        .withFederatedManagedIdentityCredential(id)
        .withCertificateFromKeyVault("https://example.vault.azure.net", "certificate")
        .withCertificateThumbprint("CurrentUser/My", "0123456789ABCDEF")
        .withCertificateDistinguishedName("CurrentUser/My", "CN=example")
        .withClientCapability("cp1")
        .withAllowWebApiToBeAuthorizedByACL()
        .withSendX5C()
        .withExtraQueryParameter("domain_hint", "contoso.com")
        .withAudience("api://example");
    existing.withCredential(builder.createEntraIdFileCertificateCredential("/certs/client.pfx", secret));
    existing.withCredential(builder.createEntraIdSignedAssertionFileCredential());
    parameterized.withCredential(builder.createEntraIdSignedAssertionFileCredential("/tokens/assertion"));
    existing.withCredential(builder.createEntraIdFederatedIdentityCredential(id,
        new CreateEntraIdFederatedIdentityCredentialOptions()
            .tokenExchangeUrl("api://AzureADTokenExchange")
            .tokenExchangeAuthority("https://login.microsoftonline.com/contoso.onmicrosoft.com")));

    var web = builder.addContainer("web", "nginx").withHttpEndpoint(new WithHttpEndpointOptions().targetPort(80));
    web.withReference(existing);
    web.withEntraIdReference(parameterized, "Identity");
    var created = builder.addEntraIdApplication("created");
    created.withRedirectUris(new String[] {"https://localhost:7123/signin-oidc"})
        .withRedirectUrisFromParameters(new ParameterResource[] {redirect})
        .withRedirectUrisForEndpoint(web.getEndpoint("http"), new String[] {"/signin-oidc"})
        .withRedirectUrisForHost(host, "/signin-oidc", new String[] {"/signout-callback-oidc"})
        .withApplicationOwners(new String[] {id})
        .withApplicationOwnersFromParameters(new ParameterResource[] {owner})
        .withServicePrincipalOwners(new String[] {id})
        .withServicePrincipalOwnersFromParameters(new ParameterResource[] {owner})
        .withOwners(new String[] {id})
        .withOwnersFromParameters(new ParameterResource[] {owner})
        .withIdTokenIssuance()
        .publishWithFederatedManagedIdentityCredential();
    web.withReference(created);
    var targeted = builder.addEntraIdApplication("targeted");
    web.withReference(targeted);
    targeted.publishWithFederatedManagedIdentityCredential(web);
    var identity = builder.addAzureUserAssignedIdentity("identity");
    var shared = builder.addEntraIdApplication("shared");
    shared.publishWithFederatedManagedIdentityCredential(identity);
    web.withAzureUserAssignedIdentity(identity).withReference(shared);
    builder.addEntraIdApplication("converted").asExistingApplication("contoso.onmicrosoft.com", id);
    builder.addEntraIdApplication("converted-parameters").asExistingApplicationFromParameters(tenant, client);
    builder.build().run();
}
