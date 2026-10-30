// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json.Nodes;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Azure.EntraId.Tests.TestServices;
using Aspire.Hosting.Tests.Utils;
using Aspire.Hosting.Utils;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Identity.Abstractions;
using Microsoft.Identity.Client;
using Microsoft.Identity.Web;

namespace Aspire.Hosting.Azure.EntraId.Tests;

public class MicrosoftIdentityWebConfigurationTests
{
    private const string TenantId = "aaaabbbb-0000-cccc-1111-dddd2222eeee";
    private const string ClientId = "00001111-aaaa-2222-bbbb-3333cccc4444";
    private const string ManagedIdentityClientId = "11112222-bbbb-3333-cccc-4444dddd5555";

    [Theory]
    [InlineData(DistributedApplicationOperation.Run, "AzureAd")]
    [InlineData(DistributedApplicationOperation.Publish, "AzureAd")]
    [InlineData(DistributedApplicationOperation.Run, "Authentication:Entra")]
    [InlineData(DistributedApplicationOperation.Publish, "Authentication:Entra")]
    public async Task WithReference_BindsToMicrosoftIdentityWebOptions(
        DistributedApplicationOperation operation,
        string sectionName)
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var secret = builder.AddParameter("EntraSecret", "test-client-secret", secret: true);
        var password = builder.AddParameter("CertificatePassword", "test-certificate-password", secret: true);
        var entra = builder.AddExistingEntraApplication("entra-web", tenantId: TenantId, clientId: ClientId)
            .WithInstance("https://login.microsoftonline.us/")
            .WithSignInAudience(EntraIdSignInAudience.AzureADMultipleOrgs)
            .WithClientSecret(secret)
            .WithCredential(new EntraIdFederatedIdentityCredential(ManagedIdentityClientId)
            {
                TokenExchangeUrl = "api://AzureADTokenExchangeUSGov",
                TokenExchangeAuthority = $"https://login.microsoftonline.us/{TenantId}"
            })
            .WithCertificateFromKeyVault("https://contoso.vault.usgovcloudapi.net", "web-certificate")
            .WithCertificateThumbprint("CurrentUser/My", "ABC123")
            .WithCertificateDistinguishedName("LocalMachine/My", "CN=Contoso")
            .WithCredential(new EntraIdFileCertificateCredential
            {
                FilePath = "/certificates/web.pfx",
                Password = password.Resource
            })
            .WithCredential(new EntraIdSignedAssertionFileCredential { FilePath = "/var/run/secrets/assertion" })
            .WithClientCapability("cp1")
            .WithAudience($"api://{ClientId}")
            .WithSendX5C()
            .WithAllowWebApiToBeAuthorizedByACL()
            .WithExtraQueryParameter("loginHint", "user@contoso.example");
        var project = builder.AddResource(new ProjectResource("web"))
            .WithReference(entra, connectionName: sectionName);

        var configuration = await GetConfigurationAsync(project.Resource, operation);
        var section = configuration.GetSection(sectionName);
        // The unified options cover settings shared by Identity.Web's authentication and token-acquisition options.
        // Strict binding catches misspelled credential keys instead of silently accepting unknown configuration.
        var options = section.Get<MicrosoftEntraApplicationOptions>(binder => binder.ErrorOnUnknownConfiguration = true);
        Assert.NotNull(options);
        Assert.Equal("https://login.microsoftonline.us/", options.Instance);
        Assert.Equal("organizations", options.TenantId);
        Assert.Equal(TenantId, options.AppHomeTenantId);
        Assert.Equal(ClientId, options.ClientId);
        Assert.True(options.SendX5C);
        Assert.True(options.AllowWebApiToBeAuthorizedByACL);
        Assert.Equal(["cp1"], options.ClientCapabilities);
        Assert.Equal([$"api://{ClientId}"], options.Audiences);
        Assert.Equal(new Dictionary<string, string> { ["loginHint"] = "user@contoso.example" }, options.ExtraQueryParameters);

        Assert.NotNull(options.ClientCredentials);
        Assert.Collection(options.ClientCredentials,
            credential =>
            {
                Assert.Equal(CredentialSource.ClientSecret, credential.SourceType);
                Assert.Equal(operation == DistributedApplicationOperation.Run ? "test-client-secret" : "{EntraSecret.value}", credential.ClientSecret);
            },
            credential =>
            {
                Assert.Equal(CredentialSource.SignedAssertionFromManagedIdentity, credential.SourceType);
                Assert.Equal(ManagedIdentityClientId, credential.ManagedIdentityClientId);
                Assert.Equal("api://AzureADTokenExchangeUSGov", credential.TokenExchangeUrl);
                Assert.Equal($"https://login.microsoftonline.us/{TenantId}", credential.TokenExchangeAuthority);
            },
            credential =>
            {
                Assert.Equal(CredentialSource.KeyVault, credential.SourceType);
                Assert.Equal("https://contoso.vault.usgovcloudapi.net", credential.KeyVaultUrl);
                Assert.Equal("web-certificate", credential.KeyVaultCertificateName);
            },
            credential =>
            {
                Assert.Equal(CredentialSource.StoreWithThumbprint, credential.SourceType);
                Assert.Equal("CurrentUser/My", credential.CertificateStorePath);
                Assert.Equal("ABC123", credential.CertificateThumbprint);
            },
            credential =>
            {
                Assert.Equal(CredentialSource.StoreWithDistinguishedName, credential.SourceType);
                Assert.Equal("LocalMachine/My", credential.CertificateStorePath);
                Assert.Equal("CN=Contoso", credential.CertificateDistinguishedName);
            },
            credential =>
            {
                Assert.Equal(CredentialSource.Path, credential.SourceType);
                Assert.Equal("/certificates/web.pfx", credential.CertificateDiskPath);
                Assert.Equal(operation == DistributedApplicationOperation.Run ? "test-certificate-password" : "{CertificatePassword.value}", credential.CertificatePassword);
            },
            credential =>
            {
                Assert.Equal(CredentialSource.SignedAssertionFilePath, credential.SourceType);
                Assert.Equal("/var/run/secrets/assertion", credential.SignedAssertionFileDiskPath);
            });

        var webOptions = section.Get<MicrosoftIdentityOptions>();
        Assert.NotNull(webOptions);
        Assert.Equal(options.ClientId, webOptions.ClientId);
        Assert.Equal(options.Instance, webOptions.Instance);
        Assert.Equal(options.TenantId, webOptions.TenantId);
        Assert.Equal(options.ClientCredentials.Select(credential => credential.SourceType),
            webOptions.ClientCredentials!.Select(credential => credential.SourceType));
    }

    [Theory]
    [InlineData(EntraIdSignInAudience.AzureADMyOrg, null)]
    [InlineData(EntraIdSignInAudience.AzureADMultipleOrgs, "organizations")]
    [InlineData(EntraIdSignInAudience.AzureADandPersonalMicrosoftAccount, "common")]
    [InlineData(EntraIdSignInAudience.PersonalMicrosoftAccount, "consumers")]
    public async Task WithReference_CreatedRegistration_BindsTenantAndClientOutputs(
        EntraIdSignInAudience audience,
        string? signInTenant)
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var entra = builder.AddEntraIdApplication("entra-web").WithSignInAudience(audience);
        entra.Resource.Outputs["tenantId"] = TenantId;
        entra.Resource.Outputs["clientId"] = ClientId;
        entra.Resource.ProvisioningTaskCompletionSource!.SetResult();
        var project = builder.AddResource(new ProjectResource("web")).WithReference(entra);

        var configuration = await GetConfigurationAsync(project.Resource, DistributedApplicationOperation.Run);
        var options = configuration.GetSection("AzureAd")
            .Get<MicrosoftEntraApplicationOptions>(binder => binder.ErrorOnUnknownConfiguration = true);

        Assert.NotNull(options);
        Assert.Equal(ClientId, options.ClientId);
        Assert.Equal(signInTenant ?? TenantId, options.TenantId);
        Assert.Equal(signInTenant is null ? null : TenantId, options.AppHomeTenantId);
        Assert.True(options.ClientCredentials is null || !options.ClientCredentials.Any());
    }

    [Fact]
    public async Task WithReference_ConfiguresMicrosoftIdentityWebSignInOnly()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var entra = builder.AddExistingEntraApplication("entra-web", tenantId: TenantId, clientId: ClientId);
        var project = builder.AddResource(new ProjectResource("web")).WithReference(entra);
        var configuration = await GetConfigurationAsync(project.Resource, DistributedApplicationOperation.Run);
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddLogging();
        services.AddAuthentication(OpenIdConnectDefaults.AuthenticationScheme)
            .AddMicrosoftIdentityWebApp(configuration.GetSection("AzureAd"));
        using var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>()
            .Get(OpenIdConnectDefaults.AuthenticationScheme);

        Assert.Equal("id_token", options.ResponseType);
        Assert.Equal("/signin-oidc", options.CallbackPath.Value);
        Assert.Equal("/signout-callback-oidc", options.SignedOutCallbackPath.Value);
        Assert.True(options.RequireHttpsMetadata);
        Assert.Equal(ClientId, options.ClientId);
    }

    [Fact]
    public async Task LocalCertificate_CanSignAuthorizationCodeRedemptionAndAcquireADownstreamToken()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var entra = builder.AddEntraIdApplication("entra-web").RunWithLocalDeveloperCertificateCredential();
        using var app = builder.Build();
        var credential = Assert.IsType<EntraIdLocalDeveloperCertificateCredential>(entra.Resource.ClientCredentials[0]);
        var selected = await credential.GetCertificateAsync(app.Services, entra.Resource, TestContext.Current.CancellationToken);
        var description = new CredentialDescription { SourceType = CredentialSource.Base64Encoded, Base64EncodedValue = selected.Base64EncodedValue };
        await new DefaultCredentialsLoader().LoadCredentialsIfNeededAsync(description);
        using var certificate = description.Certificate;
        using var publicCertificate = X509CertificateLoader.LoadCertificate(Convert.FromBase64String(selected.PublicCertificate));
        using var publicKey = publicCertificate.GetRSAPublicKey();
        Assert.NotNull(certificate);
        Assert.NotNull(publicKey);
        const string scope = "api://downstream/read";
        const string authority = $"https://login.microsoftonline.com/{TenantId}";
        var tokenRequests = 0;
        using var handler = new CertificateTestHttpMessageHandler(async (request, cancellationToken) =>
        {
            if (request.Method == HttpMethod.Get)
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new
                    {
                        issuer = $"{authority}/v2.0",
                        authorization_endpoint = $"{authority}/oauth2/v2.0/authorize",
                        token_endpoint = $"{authority}/oauth2/v2.0/token"
                    })
                };
            }

            var form = QueryHelpers.ParseQuery(await request.Content!.ReadAsStringAsync(cancellationToken));
            Assert.Equal("authorization_code", form["grant_type"]);
            Assert.Equal("test-authorization-code", form["code"]);
            Assert.Equal(ClientId, form["client_id"]);
            Assert.Equal("urn:ietf:params:oauth:client-assertion-type:jwt-bearer", form["client_assertion_type"]);
            var assertion = form["client_assertion"].ToString().Split('.');
            var header = JsonNode.Parse(WebEncoders.Base64UrlDecode(assertion[0]))!;
            var padding = header["alg"]!.GetValue<string>() == "PS256" ? RSASignaturePadding.Pss : RSASignaturePadding.Pkcs1;
            Assert.True(publicKey.VerifyData(
                Encoding.ASCII.GetBytes($"{assertion[0]}.{assertion[1]}"),
                WebEncoders.Base64UrlDecode(assertion[2]), HashAlgorithmName.SHA256, padding));
            Interlocked.Increment(ref tokenRequests);

            // This is a simulated token endpoint, not a live tenant. MSAL parses the ID token for
            // account caching; interactive OIDC signature/issuer validation is not exercised here.
            var now = DateTimeOffset.UtcNow;
            var idToken = $"{Encode(new { alg = "RS256" })}.{Encode(new { aud = ClientId, iss = $"{authority}/v2.0", sub = "test-user", oid = ClientId, tid = TenantId, preferred_username = "user@example.invalid", exp = now.AddHours(1).ToUnixTimeSeconds(), iat = now.ToUnixTimeSeconds() })}.signature";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new
                {
                    token_type = "Bearer",
                    access_token = "downstream-access-token",
                    expires_in = 3600,
                    scope,
                    id_token = idToken,
                    client_info = Encode(new { uid = ClientId, utid = TenantId })
                })
            };
        });
        using var httpClient = new HttpClient(handler);
        var client = ConfidentialClientApplicationBuilder.Create(ClientId)
            .WithAuthority(authority, validateAuthority: false)
            .WithInstanceDiscovery(false)
            .WithCertificate(certificate)
            .WithHttpClientFactory(new CertificateTestMsalHttpClientFactory(httpClient))
            .Build();

        var redeemed = await client.AcquireTokenByAuthorizationCode([scope], "test-authorization-code").ExecuteAsync(TestContext.Current.CancellationToken);
        Assert.Equal("downstream-access-token", redeemed.AccessToken);
        Assert.NotNull(redeemed.Account);
        var downstream = await client.AcquireTokenSilent([scope], redeemed.Account).ExecuteAsync(TestContext.Current.CancellationToken);
        Assert.Equal("downstream-access-token", downstream.AccessToken);
        Assert.Equal(1, tokenRequests);

        static string Encode<T>(T value) => WebEncoders.Base64UrlEncode(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(value));
    }

    private static async Task<IConfigurationRoot> GetConfigurationAsync(
        IResource resource,
        DistributedApplicationOperation operation)
    {
        var environment = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(resource, operation, TestServiceProvider.Instance);
        return new ConfigurationBuilder()
            .AddInMemoryCollection(environment.Select(entry =>
                new KeyValuePair<string, string?>(entry.Key.Replace("__", ":", StringComparison.Ordinal), entry.Value)))
            .Build();
    }
}
