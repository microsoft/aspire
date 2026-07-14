// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREPERSISTENCE001 // Resource lifetime APIs are experimental.
#pragma warning disable ASPIRECERTIFICATES001

using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Hosting.Tests;

[Trait("Partition", "2")]
public class ProjectResourceBuilderExtensionTests
{
    [Fact]
    public void WithPersistentLifetimeAddsPersistenceAnnotation()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var project = builder.AddProject<TestProject>("project", options => options.ExcludeLaunchProfile = true)
            .WithPersistentLifetime();

        var annotation = project.Resource.Annotations.OfType<PersistenceAnnotation>().Single();
        Assert.Equal(PersistenceMode.Persistent, annotation.Mode);
    }

    [Fact]
    public async Task WithProjectDefaultsAddsHttpsCertificateConfigurationForTlsEndpoints()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        builder.Configuration["Parameters:password"] = "test-password";
        var cert = CreateTestCertificateWithPrivateKey();
        var password = builder.AddParameter("password", secret: true);

        var resource = builder.AddProject<TestProject>("test", options => options.ExcludeLaunchProfile = true)
            .WithHttpsEndpoint()
            .WithHttpsCertificate(cert, password)
            .Resource;

        await builder.BuildAsync();

        var context = new ExecutionConfigurationGathererContext();
        await new HttpsCertificateExecutionConfigurationGatherer(CreateHttpsCertificateConfigurationContextFactory())
            .GatherAsync(context, resource, NullLogger.Instance, builder.ExecutionContext);

        var certificatePath = Assert.IsAssignableFrom<IValueProvider>(context.EnvironmentVariables[KnownAspNetCoreConfigNames.KestrelCertificatesDefaultPath]);
        Assert.Equal("/etc/ssl/certs/server.pfx", await certificatePath.GetValueAsync());
        Assert.Same(password.Resource, context.EnvironmentVariables[KnownAspNetCoreConfigNames.KestrelCertificatesDefaultPassword]);

        var metadata = context.AdditionalConfigurationData.OfType<HttpsCertificateExecutionConfigurationData>().Single();
        Assert.False(metadata.IsKeyPathReferenced);
        Assert.False(metadata.IsCertificateWithKeyPathReferenced);
        Assert.True(metadata.IsPfxPathReferenced);
    }

    [Fact]
    public async Task WithProjectDefaultsRemovesStaleKestrelCertificatePassword()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var cert = CreateTestCertificateWithPrivateKey();

        var resource = builder.AddProject<TestProject>("test", options => options.ExcludeLaunchProfile = true)
            .WithHttpsEndpoint()
            .WithEnvironment(KnownAspNetCoreConfigNames.KestrelCertificatesDefaultPassword, "stale-password")
            .WithHttpsCertificate(cert)
            .Resource;

        await builder.BuildAsync();

        var context = new ExecutionConfigurationGathererContext();
        await new EnvironmentVariablesExecutionConfigurationGatherer()
            .GatherAsync(context, resource, NullLogger.Instance, builder.ExecutionContext);
        await new HttpsCertificateExecutionConfigurationGatherer(CreateHttpsCertificateConfigurationContextFactory())
            .GatherAsync(context, resource, NullLogger.Instance, builder.ExecutionContext);

        var certificatePath = Assert.IsAssignableFrom<IValueProvider>(context.EnvironmentVariables[KnownAspNetCoreConfigNames.KestrelCertificatesDefaultPath]);
        Assert.Equal("/etc/ssl/certs/server.pfx", await certificatePath.GetValueAsync());
        Assert.DoesNotContain(KnownAspNetCoreConfigNames.KestrelCertificatesDefaultPassword, context.EnvironmentVariables.Keys);
    }

    [Fact]
    public async Task WithProjectDefaultsDoesNotAddHttpsCertificateConfigurationWithoutTlsEndpoints()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var cert = CreateTestCertificateWithPrivateKey();

        var resource = builder.AddProject<TestProject>("test", options => options.ExcludeLaunchProfile = true)
            .WithHttpsCertificate(cert)
            .Resource;

        await builder.BuildAsync();

        var context = new ExecutionConfigurationGathererContext();
        await new HttpsCertificateExecutionConfigurationGatherer(CreateHttpsCertificateConfigurationContextFactory())
            .GatherAsync(context, resource, NullLogger.Instance, builder.ExecutionContext);

        Assert.Empty(context.EnvironmentVariables);

        var metadata = context.AdditionalConfigurationData.OfType<HttpsCertificateExecutionConfigurationData>().Single();
        Assert.False(metadata.IsPfxPathReferenced);
    }

    private static X509Certificate2 CreateTestCertificateWithPrivateKey()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            new X500DistinguishedName("CN=test"),
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        return request.CreateSelfSigned(DateTimeOffset.Now, DateTimeOffset.Now.AddYears(1));
    }

    private static Func<X509Certificate2, HttpsCertificateExecutionConfigurationContext> CreateHttpsCertificateConfigurationContextFactory()
    {
        return cert => new HttpsCertificateExecutionConfigurationContext
        {
            CertificatePath = ReferenceExpression.Create($"/etc/ssl/certs/server.crt"),
            KeyPath = ReferenceExpression.Create($"/etc/ssl/private/server.key"),
            CertificateWithKeyPath = ReferenceExpression.Create($"/etc/ssl/certs/server.pem"),
            PfxPath = ReferenceExpression.Create($"/etc/ssl/certs/server.pfx")
        };
    }

    private sealed class TestProject : IProjectMetadata
    {
        public string ProjectPath => "test.csproj";
    }
}
