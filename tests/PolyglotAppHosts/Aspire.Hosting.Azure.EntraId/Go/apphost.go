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
	tenant := builder.AddParameter("tenant")
	client := builder.AddParameter("client")
	secret := builder.AddParameter("secret", &aspire.AddParameterOptions{Secret: aspire.BoolPtr(true)})
	owner := builder.AddParameter("owner")
	redirect := builder.AddParameter("redirect")
	host := builder.AddParameter("host")
	id := "00000000-0000-0000-0000-000000000001"

	existing := builder.AddExistingEntraApplication("existing", "contoso.onmicrosoft.com", id)
	parameterized := builder.AddExistingEntraApplicationFromParameters("parameterized", tenant, client)
	existing.WithClientSecret(secret).
		WithInstance("https://login.microsoftonline.us/").
		WithSignInAudience(aspire.EntraIdSignInAudienceAzureADMultipleOrgs).
		WithFederatedManagedIdentityCredential(id).
		WithCertificateFromKeyVault("https://example.vault.azure.net", "certificate").
		WithCertificateThumbprint("CurrentUser/My", "0123456789ABCDEF").
		WithCertificateDistinguishedName("CurrentUser/My", "CN=example").
		WithClientCapability("cp1").
		WithAllowWebApiToBeAuthorizedByACL().
		WithSendX5C().
		WithExtraQueryParameter("domain_hint", "contoso.com").
		WithAudience("api://example")
	existing.WithCredential(builder.CreateEntraIdFileCertificateCredential("/certs/client.pfx",
		&aspire.CreateEntraIdFileCertificateCredentialOptions{Password: &secret}))
	existing.WithCredential(builder.CreateEntraIdSignedAssertionFileCredential())
	parameterized.WithCredential(builder.CreateEntraIdSignedAssertionFileCredential(
		&aspire.CreateEntraIdSignedAssertionFileCredentialOptions{FilePath: aspire.StringPtr("/tokens/assertion")}))
	existing.WithCredential(builder.CreateEntraIdFederatedIdentityCredential(id,
		&aspire.CreateEntraIdFederatedIdentityCredentialOptions{
			TokenExchangeUrl:       aspire.StringPtr("api://AzureADTokenExchange"),
			TokenExchangeAuthority: aspire.StringPtr("https://login.microsoftonline.com/contoso.onmicrosoft.com"),
		}))

	web := builder.AddContainer("web", "nginx").WithHttpEndpoint(&aspire.WithHttpEndpointOptions{TargetPort: aspire.Float64Ptr(80)})
	web.WithReference(existing)
	web.WithEntraIdReference(parameterized, &aspire.WithEntraIdReferenceOptions{ConnectionName: aspire.StringPtr("Identity")})
	created := builder.AddEntraIdApplication("created")
	created.WithRedirectUris([]string{"https://localhost:7123/signin-oidc"}).
		WithRedirectUrisFromParameters([]aspire.ParameterResource{redirect}).
		WithRedirectUrisForEndpoint(web.GetEndpoint("http"), []string{"/signin-oidc"}).
		WithRedirectUrisForHost(host, "/signin-oidc", []string{"/signout-callback-oidc"}).
		WithApplicationOwners([]string{id}).
		WithApplicationOwnersFromParameters([]aspire.ParameterResource{owner}).
		WithServicePrincipalOwners([]string{id}).
		WithServicePrincipalOwnersFromParameters([]aspire.ParameterResource{owner}).
		WithOwners([]string{id}).
		WithOwnersFromParameters([]aspire.ParameterResource{owner}).
		WithIdTokenIssuance().
		PublishWithFederatedManagedIdentityCredential()
	web.WithReference(created)
	targeted := builder.AddEntraIdApplication("targeted")
	web.WithReference(targeted)
	targeted.PublishWithFederatedManagedIdentityCredential(&aspire.PublishWithFederatedManagedIdentityCredentialOptions{Identity: web})
	identity := builder.AddAzureUserAssignedIdentity("identity")
	shared := builder.AddEntraIdApplication("shared")
	shared.PublishWithFederatedManagedIdentityCredential(&aspire.PublishWithFederatedManagedIdentityCredentialOptions{Identity: identity})
	web.WithAzureUserAssignedIdentity(identity).WithReference(shared)
	builder.AddEntraIdApplication("converted").AsExistingApplication("contoso.onmicrosoft.com", id)
	builder.AddEntraIdApplication("converted-parameters").AsExistingApplicationFromParameters(tenant, client)
	app, err := builder.Build()
	if err != nil {
		log.Fatal(aspire.FormatError(err))
	}
	if err := app.Run(); err != nil {
		log.Fatal(aspire.FormatError(err))
	}
}
