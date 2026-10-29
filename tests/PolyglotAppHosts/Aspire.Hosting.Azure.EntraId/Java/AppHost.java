import aspire.*;

void main() throws Exception {
        var builder = DistributedApplication.CreateBuilder();
        var tenantId = builder.addParameter("entra-tenant");
        var clientId = builder.addParameter("entra-client");
        var clientSecret = builder.addParameter("entra-secret", new AddParameterOptions().secret(true));
        var certificatePassword = builder.addParameter("certificate-password", new AddParameterOptions().secret(true));
        var redirectUris = builder.addParameter("redirect-uris");
        var redirectHost = builder.addParameter("redirect-host");
        var ownerId = builder.addParameter("entra-owner");
        var exampleTenant = "aaaabbbb-0000-cccc-1111-dddd2222eeee";
        var exampleClient = "00001111-aaaa-2222-bbbb-3333cccc4444";
        var container = builder.addContainer("api", "mcr.microsoft.com/dotnet/samples:aspnetapp");
        container.withHttpEndpoint(new WithHttpEndpointOptions().name("http").targetPort(8080.0));
        var executable = builder.addExecutable("worker", "dotnet", ".", new String[] { "--info" });
        var endpoint = container.getEndpoint("http");
        var identity = builder.addAzureUserAssignedIdentity("identity");

        // Existing registrations: configuration only, with literal or parameter IDs in any combination.
        var existingEntra = builder.addExistingEntraApplication("existing-entra", tenantId, exampleClient);
        builder.addExistingEntraApplication("existing-entra-literals", exampleTenant, exampleClient);
        builder.addExistingEntraApplication("existing-entra-parameters", tenantId, clientId);
        existingEntra.withInstance("https://login.microsoftonline.com/");
        existingEntra.withSignInAudience(EntraIdSignInAudience.AZURE_ADMULTIPLE_ORGS);
        existingEntra.withAudience("api://example");
        existingEntra.withClientSecret(clientSecret);
        var tokenExchange = new EntraIdTokenExchangeOptions();
        tokenExchange.setTokenExchangeUrl("api://AzureADTokenExchange");
        tokenExchange.setTokenExchangeAuthority("https://login.microsoftonline.com/");
        existingEntra.withFederatedManagedIdentityCredential(exampleClient, tokenExchange);
        existingEntra.withCertificateFromKeyVault("https://vault.vault.azure.net", "certificate");
        existingEntra.withCertificateThumbprint("CurrentUser/My", "thumbprint");
        existingEntra.withCertificateDistinguishedName("CurrentUser/My", "CN=certificate");
        existingEntra.withCertificateFromFile("/certs/client.pfx", certificatePassword);
        existingEntra.withSignedAssertionFile("/tokens/assertion");
        existingEntra.withSignedAssertionFile();
        existingEntra.withSendX5C();
        existingEntra.withClientCapability("cp1");
        existingEntra.withAllowWebApiToBeAuthorizedByACL();
        existingEntra.withExtraQueryParameter("domain_hint", "contoso.com");
        container.withReference(existingEntra, new WithReferenceOptions().connectionName("AUTH"));
        executable.withReference(existingEntra);

        var fluentExistingEntra = builder.addEntraIdApplication("fluent-existing-entra");
        fluentExistingEntra.asExistingApplication(exampleTenant, clientId);

        // An existing API registration is configuration only: its URI and scope IDs describe what the registration already defines.
        var existingApi = builder.addExistingEntraApplication("existing-api", exampleTenant, exampleClient);
        existingApi.withApplicationIdUri("api://contoso-orders");
        var ordersScope = new EntraIdScopeOptions();
        ordersScope.setId("33334444-aaaa-5555-bbbb-6666cccc7777");
        existingApi.withScope("orders.read", "Read orders.", ordersScope);

        // Registrations created by Aspire.
        var createdEntra = builder.addEntraIdApplication("created-entra");
        createdEntra.withOwners(exampleClient);
        createdEntra.withOwners(ownerId);
        createdEntra.withApplicationOwners(ownerId);
        createdEntra.withServicePrincipalOwners(exampleClient);
        createdEntra.withIdTokenIssuance();
        createdEntra.withAppRole("User", "User", "Can use the API.");
        createdEntra.withAppRole("Admin", "Administrator", "Can administer the API.", new WithAppRoleOptions()
            .allowedMemberTypes(new String[] { "User", "Application" })
            .id("11112222-aaaa-3333-bbbb-4444cccc5555")
            .isEnabled(false));
        createdEntra.withRedirectUris(AspireUnion.of("https://localhost/signin"));
        createdEntra.withRedirectUris(AspireUnion.of(redirectUris));
        createdEntra.withRedirectUris(redirectHost, new String[] { "/signin", "/signout" });
        createdEntra.withRedirectUris(endpoint, new String[] { "/signin" });
        container.withReference(createdEntra, new WithReferenceOptions());
        createdEntra.publishWithFederatedManagedIdentityCredential();
        createdEntra.publishWithFederatedManagedIdentityCredential(container);
        createdEntra.publishWithFederatedManagedIdentityCredential(identity);

        // An API exposed by one registration and called by another.
        var apiEntra = builder.addEntraIdApplication("api-entra");
        apiEntra.withApplicationIdUri();
        apiEntra.withApplicationIdUri("api://contoso-api");
        apiEntra.withScope("access_as_user", "Allows the app to call the API.");
        var adminScope = new EntraIdScopeOptions();
        adminScope.setDisplayName("Administer");
        adminScope.setUserConsentDisplayName("Administer on your behalf");
        adminScope.setUserConsentDescription("Allows the app to administer the API for you.");
        adminScope.setRequiresAdminConsent(true);
        adminScope.setIsEnabled(false);
        adminScope.setId("11112222-aaaa-3333-bbbb-4444cccc5555");
        apiEntra.withScope("admin", "Allows the app to administer the API.", adminScope);
        var scopeUri = apiEntra.getScopeUri("access_as_user");
        container.withEnvironment("API_SCOPE", scopeUri);
        var preAuthorizedEntra = builder.addEntraIdApplication("pre-authorized-entra");
        apiEntra.withPreAuthorizedApplication(preAuthorizedEntra, new String[] { "access_as_user" });

        var readPermission = new EntraIdExternalApiPermission();
        readPermission.setName("User.Read");
        var readAllPermission = new EntraIdExternalApiPermission();
        readAllPermission.setName("User.Read.All");
        readAllPermission.setType(EntraIdPermissionType.APPLICATION);
        var itemsPermission = new EntraIdExternalApiPermission();
        itemsPermission.setName("items.read");
        itemsPermission.setId("22223333-aaaa-4444-bbbb-5555cccc6666");
        var contosoApi = new EntraIdCustomApi();
        contosoApi.setDisplayName("Contoso API");
        contosoApi.setApplicationId("99998888-aaaa-7777-bbbb-6666cccc5555");
        contosoApi.setResourceUri("api://contoso");
        contosoApi.setBaseUrl("https://contoso.example");
        createdEntra.withApiPermissions(apiEntra, new String[] { "access_as_user" });
        createdEntra.withAdminConsent(apiEntra, new String[] { "access_as_user" });
        createdEntra.withApiPermissions(existingApi, new String[] { "orders.read" });
        createdEntra.withExternalApiPermissions(EntraIdWellKnownApi.MICROSOFT_GRAPH, new EntraIdExternalApiPermission[] { readPermission, readAllPermission });
        createdEntra.withExternalApiPermissions(contosoApi, new EntraIdExternalApiPermission[] { itemsPermission });
        createdEntra.withExternalApiAdminConsent(EntraIdWellKnownApi.MICROSOFT_GRAPH, new EntraIdExternalApiPermission[] { readPermission, readAllPermission });
        executable.withEntraIdReference(createdEntra, new WithEntraIdReferenceOptions()
            .connectionName("AUTH")
            .downstreamApisPrefix("Apis"));
        builder.build().run();
    }
