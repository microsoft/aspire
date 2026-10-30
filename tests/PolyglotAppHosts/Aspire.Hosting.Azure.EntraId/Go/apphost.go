package main

import (
	"log"

	"apphost/modules/aspire"
)

func main() {
	builder, err := aspire.CreateBuilder()
	if err != nil {
		log.Fatal(aspire.FormatError(err))
	}

	tenantId := builder.AddParameter("entra-tenant")
	clientId := builder.AddParameter("entra-client")
	clientSecret := builder.AddParameter("entra-secret", &aspire.AddParameterOptions{Secret: aspire.BoolPtr(true)})
	certificatePassword := builder.AddParameter("certificate-password", &aspire.AddParameterOptions{Secret: aspire.BoolPtr(true)})
	redirectUris := builder.AddParameter("redirect-uris")
	redirectHost := builder.AddParameter("redirect-host")
	ownerId := builder.AddParameter("entra-owner")
	exampleTenant := "aaaabbbb-0000-cccc-1111-dddd2222eeee"
	exampleClient := "00001111-aaaa-2222-bbbb-3333cccc4444"

	container := builder.AddContainer("api", "mcr.microsoft.com/dotnet/samples:aspnetapp").
		WithHttpEndpoint(&aspire.WithHttpEndpointOptions{Name: aspire.StringPtr("http"), TargetPort: aspire.Float64Ptr(8080)})
	executable := builder.AddExecutable("worker", "dotnet", ".", []string{"--info"})
	endpoint := container.GetEndpoint("http")
	identity := builder.AddAzureUserAssignedIdentity("identity")

	// Existing registrations: configuration only, with literal or parameter IDs in any combination.
	existingEntra := builder.AddExistingEntraApplication("existing-entra", tenantId, exampleClient)
	builder.AddExistingEntraApplication("existing-entra-literals", exampleTenant, exampleClient)
	builder.AddExistingEntraApplication("existing-entra-parameters", tenantId, clientId)
	existingEntra.WithInstance("https://login.microsoftonline.com/")
	existingEntra.WithSignInAudience(aspire.EntraIdSignInAudienceAzureADMultipleOrgs)
	existingEntra.WithAudience("api://example")
	existingEntra.WithClientSecret(clientSecret)
	existingEntra.WithFederatedManagedIdentityCredential(exampleClient, &aspire.EntraIdTokenExchangeOptions{
		TokenExchangeUrl:       aspire.StringPtr("api://AzureADTokenExchange"),
		TokenExchangeAuthority: aspire.StringPtr("https://login.microsoftonline.com/"),
	})
	existingEntra.WithCertificateFromKeyVault("https://vault.vault.azure.net", "certificate")
	existingEntra.WithCertificateThumbprint("CurrentUser/My", "thumbprint")
	existingEntra.WithCertificateDistinguishedName("CurrentUser/My", "CN=certificate")
	existingEntra.WithCertificateFromFile("/certs/client.pfx", &aspire.WithCertificateFromFileOptions{Password: &certificatePassword})
	existingEntra.WithSignedAssertionFile(&aspire.WithSignedAssertionFileOptions{FilePath: aspire.StringPtr("/tokens/assertion")})
	existingEntra.WithSignedAssertionFile()
	existingEntra.WithSendX5C()
	existingEntra.WithClientCapability("cp1")
	existingEntra.WithAllowWebApiToBeAuthorizedByACL()
	existingEntra.WithExtraQueryParameter("domain_hint", "contoso.com")
	container.WithReference(existingEntra, &aspire.WithReferenceOptions{ConnectionName: aspire.StringPtr("AUTH")})
	executable.WithReference(existingEntra)

	fluentExistingEntra := builder.AddEntraIdApplication("fluent-existing-entra")
	fluentExistingEntra.AsExistingApplication(exampleTenant, clientId)

	// An existing API registration is configuration only: its URI and scope IDs describe what the registration already defines.
	existingApi := builder.AddExistingEntraApplication("existing-api", exampleTenant, exampleClient)
	existingApi.WithApplicationIdUri(&aspire.WithApplicationIdUriOptions{ApplicationIdUri: aspire.StringPtr("api://contoso-orders")})
	existingApi.WithScope("orders.read", "Read orders.", &aspire.EntraIdScopeOptions{Id: aspire.StringPtr("33334444-aaaa-5555-bbbb-6666cccc7777")})

	// Registrations created by Aspire.
	createdEntra := builder.AddEntraIdApplication("created-entra")
	createdEntra.WithOwners(exampleClient)
	createdEntra.WithOwners(ownerId)
	createdEntra.WithApplicationOwners(ownerId)
	createdEntra.WithServicePrincipalOwners(exampleClient)
	createdEntra.WithIdTokenIssuance()
	createdEntra.WithAppRole("User", "User", "Can use the API.")
	createdEntra.WithAppRole("Admin", "Administrator", "Can administer the API.", &aspire.WithAppRoleOptions{
		AllowedMemberTypes: []string{"User", "Application"},
		Id:                 aspire.StringPtr("11112222-aaaa-3333-bbbb-4444cccc5555"),
		IsEnabled:          aspire.BoolPtr(false),
	})
	createdEntra.WithRedirectUris("https://localhost/signin")
	createdEntra.WithRedirectUris(redirectUris)
	createdEntra.WithRedirectUris(redirectHost, &aspire.WithRedirectUrisOptions{CallbackPaths: []string{"/signin", "/signout"}})
	createdEntra.WithRedirectUris(endpoint, &aspire.WithRedirectUrisOptions{CallbackPaths: []string{"/signin"}})
	container.WithReference(createdEntra)
	createdEntra.RunWithLocalDeveloperCertificateCredential()
	createdEntra.PublishWithFederatedManagedIdentityCredential()
	createdEntra.PublishWithFederatedManagedIdentityCredential(&aspire.PublishWithFederatedManagedIdentityCredentialOptions{Identity: container})
	createdEntra.PublishWithFederatedManagedIdentityCredential(&aspire.PublishWithFederatedManagedIdentityCredentialOptions{Identity: identity})

	// An API exposed by one registration and called by another.
	apiEntra := builder.AddEntraIdApplication("api-entra")
	apiEntra.WithApplicationIdUri()
	apiEntra.WithApplicationIdUri(&aspire.WithApplicationIdUriOptions{ApplicationIdUri: aspire.StringPtr("api://contoso-api")})
	apiEntra.WithScope("access_as_user", "Allows the app to call the API.")
	apiEntra.WithScope("admin", "Allows the app to administer the API.", &aspire.EntraIdScopeOptions{
		DisplayName:            aspire.StringPtr("Administer"),
		UserConsentDisplayName: aspire.StringPtr("Administer on your behalf"),
		UserConsentDescription: aspire.StringPtr("Allows the app to administer the API for you."),
		RequiresAdminConsent:   aspire.BoolPtr(true),
		IsEnabled:              aspire.BoolPtr(false),
		Id:                     aspire.StringPtr("11112222-aaaa-3333-bbbb-4444cccc5555"),
	})
	scopeUri := apiEntra.GetScopeUri("access_as_user")
	container.WithEnvironment("API_SCOPE", scopeUri)
	preAuthorizedEntra := builder.AddEntraIdApplication("pre-authorized-entra")
	apiEntra.WithPreAuthorizedApplication(preAuthorizedEntra, []string{"access_as_user"})

	applicationPermission := aspire.EntraIdPermissionTypeApplication
	createdEntra.WithApiPermissions(apiEntra, []string{"access_as_user"})
	createdEntra.WithAdminConsent(apiEntra, []string{"access_as_user"})
	createdEntra.WithApiPermissions(existingApi, []string{"orders.read"})
	createdEntra.WithExternalApiPermissions(aspire.EntraIdWellKnownApiMicrosoftGraph, []*aspire.EntraIdExternalApiPermission{
		{Name: "User.Read"},
		{Name: "User.Read.All", Type: &applicationPermission},
	})
	createdEntra.WithExternalApiPermissions(&aspire.EntraIdCustomApi{
		DisplayName:   "Contoso API",
		ApplicationId: "99998888-aaaa-7777-bbbb-6666cccc5555",
		ResourceUri:   "api://contoso",
		BaseUrl:       aspire.StringPtr("https://contoso.example"),
	}, []*aspire.EntraIdExternalApiPermission{
		{Name: "items.read", Id: aspire.StringPtr("22223333-aaaa-4444-bbbb-5555cccc6666")},
	})
	createdEntra.WithExternalApiAdminConsent(aspire.EntraIdWellKnownApiMicrosoftGraph, []*aspire.EntraIdExternalApiPermission{
		{Name: "User.Read"},
		{Name: "User.Read.All", Type: &applicationPermission},
	})
	executable.WithEntraIdReference(createdEntra, &aspire.WithEntraIdReferenceOptions{
		ConnectionName:       aspire.StringPtr("AUTH"),
		DownstreamApisPrefix: aspire.StringPtr("Apis"),
	})

	app, err := builder.Build()
	if err != nil {
		log.Fatal(aspire.FormatError(err))
	}
	if err := app.Run(); err != nil {
		log.Fatal(aspire.FormatError(err))
	}
}
