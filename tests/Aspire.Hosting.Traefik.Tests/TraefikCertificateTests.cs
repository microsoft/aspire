// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREPIPELINES003
#pragma warning disable ASPIRECERTIFICATES001

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Publishing;
using Aspire.Hosting.Tests;
using Aspire.Hosting.Tests.Utils;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting.Traefik.Tests;

public class TraefikCertificateTests(ITestOutputHelper testOutputHelper)
{
    [Fact]
    public async Task PublishWithDnsChallengeWildcardDomainAndSecondResolver()
    {
        using var workspace = TemporaryWorkspace.Create(testOutputHelper);
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, workspace.Path);
        builder.Services.AddSingleton<IResourceContainerImageManager, MockImageBuilder>();

        builder.AddDockerComposeEnvironment("compose")
            .WithDashboard(enabled: false);

        var domain = builder.AddParameter("domain");
        var token = builder.AddParameter("cloudflare-token", secret: true);

        var traefik = builder.AddTraefik("traefik")
            .WithLetsEncrypt("admin@example.com")
            .WithDnsChallenge("cloudflare", dns =>
            {
                dns.Credentials["CF_DNS_API_TOKEN"] = token;
                dns.Resolvers.Add("1.1.1.1:53");
                dns.Resolvers.Add("8.8.8.8:53");
                dns.DelayBeforeChecks = TimeSpan.FromSeconds(10);
            })
            .WithTlsDomain(ReferenceExpression.Create($"{domain}"), ReferenceExpression.Create($"*.{domain}"))
            .WithLetsEncrypt("admin@example.com", resolverName: "http", challenge: TraefikAcmeChallenge.Http);

        var api = builder.AddContainer("api", "myregistry/api")
            .WithHttpEndpoint(targetPort: 8080)
            .WithTraefikRoute(traefik, ReferenceExpression.Create($"api.{domain}"))
            .WithTraefikRoute(traefik, route =>
            {
                // A host outside the wildcard domain, with a certificate from the HTTP-01 resolver.
                route.Host = ReferenceExpression.Create($"api.example.org");
                route.CertResolver = "http";
            });

        using var app = builder.Build();
        app.Run();

        await Verify(File.ReadAllText(Path.Combine(workspace.Path, "docker-compose.yaml")), "yaml");
    }

    [Fact]
    public async Task RunModeDoesNotRequestCertificatesOrAskForDnsCredentials()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        // No value: run mode must not need it.
        var token = builder.AddParameter("cloudflare-token", secret: true);
        var traefik = builder.AddTraefik("traefik")
            .WithLetsEncrypt("admin@example.com")
            .WithDnsChallenge("cloudflare", dns => dns.Credentials["CF_DNS_API_TOKEN"] = token)
            .WithTlsDomain("example.com", "*.example.com");

        using var app = builder.Build();

        var args = await ArgumentEvaluator.GetArgumentListAsync(traefik.Resource, app.Services);
        var environment = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(traefik.Resource, DistributedApplicationOperation.Run, app.Services);

        Assert.False(environment.ContainsKey("CF_DNS_API_TOKEN"));
        await Verify(args);
    }

    [Fact]
    public async Task WildcardDomainWithoutDnsChallengeThrows()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var traefik = builder.AddTraefik("traefik")
            .WithLetsEncrypt("admin@example.com")
            .WithTlsDomain("example.com", "*.example.com");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () => await ArgumentEvaluator.GetArgumentListAsync(traefik.Resource));
        Assert.Equal("Traefik resource 'traefik' has a wildcard TLS domain, which Let's Encrypt only issues with the DNS-01 challenge. Call WithDnsChallenge for the resolver 'letsencrypt'.", exception.Message);
    }

    [Fact]
    public void DnsChallengeWithoutResolverThrows()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var traefik = builder.AddTraefik("traefik");

        var missing = Assert.Throws<InvalidOperationException>(() => traefik.WithDnsChallenge("cloudflare"));
        Assert.Equal("Traefik resource 'traefik' has no certificate resolver for the DNS challenge. Call WithLetsEncrypt first.", missing.Message);

        traefik.WithLetsEncrypt("admin@example.com");

        var named = Assert.Throws<InvalidOperationException>(() => traefik.WithDnsChallenge("cloudflare", dns => dns.ResolverName = "dns"));
        Assert.Equal("Traefik resource 'traefik' has no certificate resolver named 'dns'. Call WithLetsEncrypt with that name first.", named.Message);

        var provider = Assert.Throws<ArgumentException>(() => traefik.WithDnsChallenge("Cloud Flare"));
        Assert.Equal("provider", provider.ParamName);

        var server = Assert.Throws<ArgumentException>(() => traefik.WithDnsChallenge("cloudflare", dns => dns.Resolvers.Add("1.1.1.1")));
        Assert.Equal("The DNS resolver '1.1.1.1' is invalid. Use host:port, for example '1.1.1.1:53'. (Parameter 'configure')", server.Message);
    }

    [Fact]
    public void RouteWithUnknownCertResolverThrows()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var traefik = builder.AddTraefik("traefik")
            .WithLetsEncrypt("admin@example.com");

        builder.AddContainer("api", "image")
            .WithHttpEndpoint(targetPort: 8080)
            .WithTraefikRoute(traefik, route =>
            {
                route.Host = ReferenceExpression.Create($"api.dev.localhost");
                route.CertResolver = "dns";
            });

        using var app = builder.Build();

        var exception = Assert.Throws<InvalidOperationException>(() => TraefikRoutingSubscriber.ApplyRoutes(app.Services.GetRequiredService<DistributedApplicationModel>(), isPublishMode: false));
        Assert.Equal("The Traefik HTTP route 'api' of resource 'api' uses the certificate resolver 'dns', which 'traefik' doesn't have. Add it with WithLetsEncrypt.", exception.Message);
    }

    [Fact]
    public async Task RunModeServesTheAspireHttpsCertificate()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var traefik = builder.AddTraefik("traefik")
            .WithLetsEncrypt("admin@example.com");

        var withoutPassword = await InvokeCertificateCallbackAsync(traefik.Resource, builder, password: null);
        var withPassword = await InvokeCertificateCallbackAsync(traefik.Resource, builder, password: builder.AddParameter("pfx-password", "secret", secret: true).Resource);

        await Verify(new
        {
            WithoutPassword = withoutPassword,
            // A password-protected key can't be read by Traefik, which keeps its own default certificate.
            WithPassword = withPassword,
            TlsFile = TraefikDynamicConfiguration.TlsFileContent,
        });
    }

    [Fact]
    public void PublishDoesNotUseTheAspireHttpsCertificate()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var traefik = builder.AddTraefik("traefik")
            .WithLetsEncrypt("admin@example.com");

        // Published Traefik gets its certificates from Let's Encrypt.
        Assert.Empty(traefik.Resource.Annotations.OfType<HttpsCertificateConfigurationCallbackAnnotation>());
    }

    private static async Task<Dictionary<string, string?>> InvokeCertificateCallbackAsync(TraefikResource traefik, IDistributedApplicationBuilder builder, IValueProvider? password)
    {
        var context = new HttpsCertificateConfigurationCallbackAnnotationContext
        {
            ExecutionContext = builder.ExecutionContext,
            Resource = traefik,
            Arguments = [],
            EnvironmentVariables = [],
            CertificatePath = ReferenceExpression.Create($"/certs/ABC.crt"),
            KeyPath = ReferenceExpression.Create($"/certs/ABC.key"),
            CertificateWithKeyPath = ReferenceExpression.Create($"/certs/ABC.pem"),
            PfxPath = ReferenceExpression.Create($"/certs/ABC.pfx"),
            Password = password,
            CancellationToken = CancellationToken.None,
        };

        foreach (var annotation in traefik.Annotations.OfType<HttpsCertificateConfigurationCallbackAnnotation>())
        {
            await annotation.Callback(context);
        }

        var values = new Dictionary<string, string?>();
        foreach (var (name, value) in context.EnvironmentVariables)
        {
            values[name] = value is ReferenceExpression expression ? await expression.GetValueAsync(CancellationToken.None) : value.ToString();
        }

        return values;
    }
}
