// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Azure.EntraId.Tests.TestServices;
using Aspire.Hosting.Testing;
using Aspire.Hosting.Tests.Utils;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Identity.Abstractions;
using Microsoft.Identity.Web;

namespace Aspire.Hosting.Azure.EntraId.Tests;

public class LocalDeveloperCertificateTests
{
    [Fact]
    public async Task Store_CreatesReusableTwoDayCertificateWithPrivateKey()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var clock = new CertificateTestTimeProvider();
        using var app = BuildApplication(builder, clock);
        var store = app.Services.GetRequiredService<LocalDeveloperCertificateStore>();

        var first = await store.GetCertificateAsync("entra-web", TestContext.Current.CancellationToken);
        var second = await store.GetCertificateAsync("entra-web", TestContext.Current.CancellationToken);

        Assert.Equal(clock.UtcNow, first.NotBefore);
        Assert.Equal(clock.UtcNow.AddDays(2), first.NotAfter);
        Assert.Equal(first.Thumbprint, second.Thumbprint);
        Assert.Equal(first.Base64EncodedValue, second.Base64EncodedValue);
        using var privateCertificate = X509CertificateLoader.LoadPkcs12(Convert.FromBase64String(first.Base64EncodedValue), null,
            OperatingSystem.IsMacOS() ? X509KeyStorageFlags.DefaultKeySet : X509KeyStorageFlags.EphemeralKeySet);
        using var publicCertificate = X509CertificateLoader.LoadCertificate(Convert.FromBase64String(first.PublicCertificate));
        using var privateKey = privateCertificate.GetRSAPrivateKey();
        using var publicKey = publicCertificate.GetRSAPublicKey();
        Assert.NotNull(privateKey);
        Assert.NotNull(publicKey);
        Assert.False(publicCertificate.HasPrivateKey);
        Assert.Collection(publicCertificate.Extensions,
            extension =>
            {
                var usage = Assert.IsType<X509KeyUsageExtension>(extension);
                Assert.Equal(X509KeyUsageFlags.DigitalSignature, usage.KeyUsages);
                Assert.True(usage.Critical);
            },
            extension => Assert.IsType<X509SubjectKeyIdentifierExtension>(extension),
            extension => Assert.IsType<X509AuthorityKeyIdentifierExtension>(extension));
        var signature = privateKey.SignData([1, 2, 3], HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        Assert.True(publicKey.VerifyData(new byte[] { 1, 2, 3 }, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
        Assert.Equal(
            Path.Combine(app.Services.GetRequiredService<IAspireStore>().BasePath, "dev-certs", "Entra", "entra-web.pfx"),
            store.GetPath("entra-web"));
    }

    [Theory]
    [InlineData(86399, false)]
    [InlineData(86400, true)]
    [InlineData(86401, true)]
    [InlineData(172801, true)]
    public async Task Store_RenewsAtOneDayRemainingAndOverwritesTheSameFile(int elapsedSeconds, bool renews)
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var clock = new CertificateTestTimeProvider();
        using var app = BuildApplication(builder, clock);
        var store = app.Services.GetRequiredService<LocalDeveloperCertificateStore>();
        var first = await store.GetCertificateAsync("entra-web", TestContext.Current.CancellationToken);
        var path = store.GetPath("entra-web");

        clock.UtcNow = clock.UtcNow.AddSeconds(elapsedSeconds);
        var second = await store.GetCertificateAsync("entra-web", TestContext.Current.CancellationToken);

        Assert.Equal(renews, first.Thumbprint != second.Thumbprint);
        Assert.Equal(second.Base64EncodedValue, Convert.ToBase64String(await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken)));
        Assert.Equal(["entra-web.pfx", "entra-web.pfx.lock"], Directory.GetFiles(Path.GetDirectoryName(path)!).Select(Path.GetFileName).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Store_IsolatesResourcesAndAppHosts()
    {
        using var firstBuilder = TestDistributedApplicationBuilder.Create();
        using var secondBuilder = TestDistributedApplicationBuilder.Create();
        using var firstApp = BuildApplication(firstBuilder, new CertificateTestTimeProvider());
        using var secondApp = BuildApplication(secondBuilder, new CertificateTestTimeProvider());
        var firstStore = firstApp.Services.GetRequiredService<LocalDeveloperCertificateStore>();
        var secondStore = secondApp.Services.GetRequiredService<LocalDeveloperCertificateStore>();

        var first = await firstStore.GetCertificateAsync("entra-web", TestContext.Current.CancellationToken);
        var otherResource = await firstStore.GetCertificateAsync("entra-api", TestContext.Current.CancellationToken);
        var otherAppHost = await secondStore.GetCertificateAsync("entra-web", TestContext.Current.CancellationToken);

        Assert.NotEqual(first.Thumbprint, otherResource.Thumbprint);
        Assert.NotEqual(first.Thumbprint, otherAppHost.Thumbprint);
        Assert.NotEqual(firstStore.GetPath("entra-web"), secondStore.GetPath("entra-web"));
    }

    [Fact]
    public async Task Store_ConcurrentCallersSelectTheSameCertificate()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        using var app = BuildApplication(builder, new CertificateTestTimeProvider());
        var store = app.Services.GetRequiredService<LocalDeveloperCertificateStore>();
        var token = TestContext.Current.CancellationToken;
        var certificates = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => store.GetCertificateAsync("entra-web", token), token)));

        Assert.Single(certificates.Select(c => c.Thumbprint).Distinct());
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(store.GetPath("entra-web"))!, "*.pfx"));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("corrupt")]
    [InlineData("public-only")]
    [InlineData("wrong-resource")]
    public async Task Store_RecoversMissingOrIncompatibleMaterial(string scenario)
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        using var app = BuildApplication(builder, new CertificateTestTimeProvider());
        var store = app.Services.GetRequiredService<LocalDeveloperCertificateStore>();
        var first = await store.GetCertificateAsync("entra-web", TestContext.Current.CancellationToken);
        var path = store.GetPath("entra-web");
        switch (scenario)
        {
            case "missing":
                File.Delete(path);
                break;
            case "corrupt":
                await File.WriteAllBytesAsync(path, [1, 2, 3], TestContext.Current.CancellationToken);
                break;
            case "public-only":
                using (var certificate = X509CertificateLoader.LoadCertificate(Convert.FromBase64String(first.PublicCertificate)))
                {
                    await File.WriteAllBytesAsync(path, certificate.Export(X509ContentType.Pfx), TestContext.Current.CancellationToken);
                }
                break;
            case "wrong-resource":
                File.Copy(store.GetPath("entra-web"), store.GetPath("entra-api"));
                path = store.GetPath("entra-api");
                break;
        }

        var replacement = await store.GetCertificateAsync(scenario == "wrong-resource" ? "entra-api" : "entra-web", TestContext.Current.CancellationToken);

        Assert.NotEqual(first.Thumbprint, replacement.Thumbprint);
        Assert.Equal(replacement.Base64EncodedValue, Convert.ToBase64String(await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task Store_CancellationWhileLockedPreservesTheOldFile()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var clock = new CertificateTestTimeProvider();
        using var app = BuildApplication(builder, clock);
        var store = app.Services.GetRequiredService<LocalDeveloperCertificateStore>();
        var first = await store.GetCertificateAsync("entra-web", TestContext.Current.CancellationToken);
        clock.UtcNow = clock.UtcNow.AddDays(1);
        var path = store.GetPath("entra-web");
        using var fileLock = new FileStream(path + ".lock", FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var pending = store.GetCertificateAsync("entra-web", cancellation.Token);
        Assert.False(pending.IsCompleted);

        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(first.Base64EncodedValue, Convert.ToBase64String(await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken)));
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public async Task Store_ReplacementFailurePreservesTheOldFileAndCanBeRetried()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var clock = new CertificateTestTimeProvider();
        using var app = BuildApplication(builder, clock);
        var store = app.Services.GetRequiredService<LocalDeveloperCertificateStore>();
        var first = await store.GetCertificateAsync("entra-web", TestContext.Current.CancellationToken);
        clock.UtcNow = clock.UtcNow.AddDays(1);
        var path = store.GetPath("entra-web");
        Directory.CreateDirectory(path + ".tmp");

        await Assert.ThrowsAnyAsync<UnauthorizedAccessException>(() => store.GetCertificateAsync("entra-web", TestContext.Current.CancellationToken));

        Assert.Equal(first.Base64EncodedValue, Convert.ToBase64String(await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken)));
        Directory.Delete(path + ".tmp");
        var replacement = await store.GetCertificateAsync("entra-web", TestContext.Current.CancellationToken);
        Assert.NotEqual(first.Thumbprint, replacement.Thumbprint);
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public async Task Store_RemovesAnInterruptedWriteEvenWhenReusingTheCertificate()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        using var app = BuildApplication(builder, new CertificateTestTimeProvider());
        var store = app.Services.GetRequiredService<LocalDeveloperCertificateStore>();
        var first = await store.GetCertificateAsync("entra-web", TestContext.Current.CancellationToken);
        var temporaryPath = store.GetPath("entra-web") + ".tmp";
        await File.WriteAllBytesAsync(temporaryPath, [1, 2, 3], TestContext.Current.CancellationToken);

        var reused = await store.GetCertificateAsync("entra-web", TestContext.Current.CancellationToken);

        Assert.Equal(first.Thumbprint, reused.Thumbprint);
        Assert.False(File.Exists(temporaryPath));
    }

    [Fact]
    public async Task Store_RestrictsPrivateFilePermissions()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        using var app = BuildApplication(builder, new CertificateTestTimeProvider());
        var store = app.Services.GetRequiredService<LocalDeveloperCertificateStore>();
        await store.GetCertificateAsync("entra-web", TestContext.Current.CancellationToken);
        var path = store.GetPath("entra-web");
        if (OperatingSystem.IsWindows())
        {
            var security = new FileInfo(path).GetAccessControl();
            Assert.True(security.AreAccessRulesProtected);
            var rule = Assert.Single(security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>());
            Assert.Equal(WindowsIdentity.GetCurrent().User, rule.IdentityReference);
            Assert.Equal(AccessControlType.Allow, rule.AccessControlType);
            Assert.Equal(FileSystemRights.FullControl, rule.FileSystemRights);
        }
        else
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(Path.GetDirectoryName(path)!));
        }
    }

    [Fact]
    public async Task RunWithLocalDeveloperCertificateCredential_IsIdempotentAndDoesNotGenerateDuringModelConstruction()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var entra = builder.AddEntraIdApplication("entra-web");

        Assert.Same(entra, entra.RunWithLocalDeveloperCertificateCredential());
        Assert.Same(entra, entra.RunWithLocalDeveloperCertificateCredential());
        var credential = Assert.IsType<EntraIdLocalDeveloperCertificateCredential>(Assert.Single(entra.Resource.ClientCredentials));
        using var app = BuildApplication(builder, new CertificateTestTimeProvider());
        Assert.False(Directory.Exists(Path.Combine(app.Services.GetRequiredService<IAspireStore>().BasePath, "dev-certs")));
        var template = entra.Resource.GetBicepTemplateString();
        var certificate = await credential.GetCertificateAsync(app.Services, entra.Resource, TestContext.Current.CancellationToken);
        await Verify(template, "bicep").AddScrubber(text => text
            .Replace(certificate.PublicCertificate, "<public DER certificate>")
            .Replace(certificate.Thumbprint, "<certificate thumbprint>"));
    }

    [Fact]
    public void RunWithLocalDeveloperCertificateCredential_RejectsExistingApplicationsInBothFluentOrders()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var tenant = Guid.NewGuid().ToString();
        var client = Guid.NewGuid().ToString();
        var existing = builder.AddExistingEntraApplication("existing", tenant, client);
        Assert.Throws<InvalidOperationException>(existing.RunWithLocalDeveloperCertificateCredential);
        var created = builder.AddEntraIdApplication("created").RunWithLocalDeveloperCertificateCredential();
        Assert.Throws<InvalidOperationException>(() => created.AsExistingApplication(tenant, client));
        Assert.Throws<InvalidOperationException>(() => created.AsExistingApplicationForExport(tenant, client));
        Assert.Null(created.Resource.TenantId);
        Assert.Null(created.Resource.ClientId);
        var tenantParameter = builder.AddParameter("tenant-id", tenant);
        var clientParameter = builder.AddParameter("client-id", client);
        Assert.Throws<InvalidOperationException>(() => created.AsExistingApplication(tenantParameter, clientParameter));
        Assert.Null(created.Resource.TenantIdParameter);
        Assert.Null(created.Resource.ClientIdParameter);
        Assert.False(created.Resource.IsExistingApplication);
    }

    [Fact]
    public async Task Publish_DoesNotRegisterCertificateServicesOrPublishCertificateConfiguration()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var entra = builder.AddEntraIdApplication("entra-web").RunWithLocalDeveloperCertificateCredential();
        var web = builder.AddResource(new ProjectResource("web")).WithReference(entra);
        var template = entra.Resource.GetBicepTemplateString();
        Assert.Empty(entra.Resource.ClientCredentials);
        Assert.All(builder.Services, s => Assert.NotEqual(typeof(LocalDeveloperCertificateStore), s.ServiceType));
        await Verify(template, "bicep");
        var environment = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(web.Resource, DistributedApplicationOperation.Publish, TestServiceProvider.Instance);
        await Verify(environment).UseMethodName($"{nameof(Publish_DoesNotRegisterCertificateServicesOrPublishCertificateConfiguration)}_Environment");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WithReference_UsesMatchingSecretBase64PfxAndMicrosoftIdentityWebCanLoadItsPrivateKey(bool container)
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var entra = builder.AddEntraIdApplication("entra-web")
            .RunWithLocalDeveloperCertificateCredential()
            .PublishWithFederatedManagedIdentityCredential();
        IResourceBuilder<IResourceWithEnvironment> web = container
            ? builder.AddContainer("web", "test-image").WithReference(entra)
            : builder.AddResource(new ProjectResource("web")).WithReference(entra);
        var executable = builder.AddExecutable("sensitivity-check", "unused", ".");
        using var app = builder.Build();
        entra.Resource.Outputs["tenantId"] = "aaaabbbb-0000-cccc-1111-dddd2222eeee";
        entra.Resource.Outputs["clientId"] = "00001111-aaaa-2222-bbbb-3333cccc4444";
        entra.Resource.ProvisioningTaskCompletionSource!.SetResult();

        var result = await ExecutionConfigurationBuilder.Create(web.Resource).WithEnvironmentVariablesConfig()
            .BuildAsync(builder.ExecutionContext, NullLogger.Instance, TestContext.Current.CancellationToken);
        Assert.Null(result.Exception);
        var environment = result.EnvironmentVariables.ToDictionary();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(container
            // Non-.NET resources retain the integration's existing upper-snake-case environment convention.
            ? new Dictionary<string, string?>
            {
                ["AzureAd:ClientCredentials:0:SourceType"] = environment["ENTRA_WEB_CLIENT_CREDENTIALS_0_SOURCE_TYPE"],
                ["AzureAd:ClientCredentials:0:Base64EncodedValue"] = environment["ENTRA_WEB_CLIENT_CREDENTIALS_0_BASE64_ENCODED_VALUE"]
            }
            : environment.Select(e => new KeyValuePair<string, string?>(e.Key.Replace("__", ":", StringComparison.Ordinal), e.Value))).Build();
        var options = configuration.GetSection("AzureAd").Get<MicrosoftEntraApplicationOptions>(b => b.ErrorOnUnknownConfiguration = true);
        var description = Assert.Single(Assert.IsType<MicrosoftEntraApplicationOptions>(options).ClientCredentials!);
        Assert.Equal(CredentialSource.Base64Encoded, description.SourceType);
        var selected = await Assert.IsType<EntraIdLocalDeveloperCertificateCredential>(Assert.Single(entra.Resource.ClientCredentials))
            .GetCertificateAsync(app.Services, entra.Resource, TestContext.Current.CancellationToken);
        Assert.Equal(selected.Base64EncodedValue, description.Base64EncodedValue);
        await new DefaultCredentialsLoader().LoadCredentialsIfNeededAsync(description);
        using var certificate = description.Certificate;
        Assert.NotNull(certificate);
        using var key = certificate.GetRSAPrivateKey();
        Assert.NotNull(key);
        using var publicCertificate = X509CertificateLoader.LoadCertificate(Convert.FromBase64String(selected.PublicCertificate));
        using var publicKey = publicCertificate.GetRSAPublicKey();
        var signature = key.SignData([1, 2, 3], HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        Assert.True(publicKey!.VerifyData(new byte[] { 1, 2, 3 }, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));

        var secret = Assert.IsType<EntraIdLocalDeveloperCertificateCredential>(entra.Resource.ClientCredentials[0]).Secret;
        Assert.True(secret.Secret);
        var dependencies = await web.Resource.GetResourceDependenciesAsync(builder.ExecutionContext, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains(secret, dependencies);
        var valueKey = container ? "ENTRA_WEB_CLIENT_CREDENTIALS_0_BASE64_ENCODED_VALUE" : "AzureAd__ClientCredentials__0__Base64EncodedValue";
        Assert.Same(secret, result.EnvironmentVariablesWithUnprocessed.Single(e => e.Key == valueKey).Value.Unprocessed);
        executable.WithArgs(secret);
        var args = await ExecutionConfigurationBuilder.Create(executable.Resource).WithArgumentsConfig()
            .BuildAsync(builder.ExecutionContext, NullLogger.Instance, TestContext.Current.CancellationToken);
        Assert.Null(args.Exception);
        Assert.True(Assert.Single(args.Arguments).IsSensitive);
    }

    [Fact]
    public async Task Credential_KeepsOneSelectionForTheRunAndResolvesWithoutProvisioning()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var clock = new CertificateTestTimeProvider();
        var entra = builder.AddEntraIdApplication("entra-web").RunWithLocalDeveloperCertificateCredential();
        using var app = BuildApplication(builder, clock);
        var credential = Assert.IsType<EntraIdLocalDeveloperCertificateCredential>(entra.Resource.ClientCredentials[0]);
        var first = await credential.GetCertificateAsync(app.Services, entra.Resource, TestContext.Current.CancellationToken);
        clock.UtcNow = clock.UtcNow.AddDays(1);
        var second = await credential.GetCertificateAsync(app.Services, entra.Resource, TestContext.Current.CancellationToken);
        Assert.Same(first, second);

        var env = new Dictionary<string, object>();
        credential.EmitEnvironmentVariables(env, "AzureAd__ClientCredentials__0", "__", entra.Resource, new ProjectResource("web"), builder.ExecutionContext);
        Assert.Same(credential.Secret, env["AzureAd__ClientCredentials__0__Base64EncodedValue"]);
        Assert.False(entra.Resource.ProvisioningTaskCompletionSource!.Task.IsCompleted);
        Assert.Equal(first.Base64EncodedValue, await credential.Secret.GetValueAsync(TestContext.Current.CancellationToken));
        Assert.False(entra.Resource.ProvisioningTaskCompletionSource.Task.IsCompleted);
    }

    private static DistributedApplication BuildApplication(IDistributedApplicationTestingBuilder builder, CertificateTestTimeProvider clock)
    {
        builder.Services.AddSingleton<TimeProvider>(clock);
        builder.Services.AddSingleton<LocalDeveloperCertificateStore>();
        return builder.Build();
    }
}
