// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIRECERTIFICATES001

using System.Collections.Immutable;
using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Hosting.Tests;

[Trait("Partition", "2")]
public class ExecutionConfigurationGathererTests
{
    #region ArgumentsExecutionConfigurationGatherer Tests

    [Fact]
    public async Task ArgumentsExecutionConfigurationGatherer_WithCommandLineArgsCallback_GathersArguments()
    {
        // Arrange
        using var builder = TestDistributedApplicationBuilder.Create();
        var resource = builder.AddExecutable("test", "test.exe", ".")
            .WithArgs("arg1", "arg2")
            .Resource;

        await builder.BuildAsync();

        var context = new ExecutionConfigurationGathererContext();
        var gatherer = new ArgumentsExecutionConfigurationGatherer();

        // Act
        await gatherer.GatherAsync(context, resource, NullLogger.Instance, builder.ExecutionContext);
        // Assert
        Assert.Equal(2, context.Arguments.Count);
        Assert.Equal("arg1", context.Arguments[0]);
        Assert.Equal("arg2", context.Arguments[1]);
    }

    [Fact]
    public async Task ArgumentsExecutionConfigurationGatherer_WithMultipleCallbacks_GathersAllArguments()
    {
        // Arrange
        using var builder = TestDistributedApplicationBuilder.Create();
        var resource = builder.AddExecutable("test", "test.exe", ".")
            .WithArgs("arg1")
            .WithArgs(ctx => ctx.Args.Add("arg2"))
            .Resource;

        await builder.BuildAsync();

        var context = new ExecutionConfigurationGathererContext();
        var gatherer = new ArgumentsExecutionConfigurationGatherer();

        // Act
        await gatherer.GatherAsync(context, resource, NullLogger.Instance, builder.ExecutionContext);
        // Assert
        Assert.Equal(2, context.Arguments.Count);
        Assert.Equal("arg1", context.Arguments[0]);
        Assert.Equal("arg2", context.Arguments[1]);
    }

    [Fact]
    public async Task ArgumentsExecutionConfigurationGatherer_NoArgsAnnotations_DoesNothing()
    {
        // Arrange
        using var builder = TestDistributedApplicationBuilder.Create();
        var resource = builder.AddExecutable("test", "test.exe", ".").Resource;

        await builder.BuildAsync();

        var context = new ExecutionConfigurationGathererContext();
        var gatherer = new ArgumentsExecutionConfigurationGatherer();

        // Act
        await gatherer.GatherAsync(context, resource, NullLogger.Instance, builder.ExecutionContext);
        // Assert
        Assert.Empty(context.Arguments);
    }

    [Fact]
    public async Task ArgumentsExecutionConfigurationGatherer_AsyncCallback_ExecutesCorrectly()
    {
        // Arrange
        using var builder = TestDistributedApplicationBuilder.Create();
        var resource = builder.AddExecutable("test", "test.exe", ".")
            .WithArgs(async ctx =>
            {
                await Task.Delay(1);
                ctx.Args.Add("async-arg");
            })
            .Resource;

        await builder.BuildAsync();

        var context = new ExecutionConfigurationGathererContext();
        var gatherer = new ArgumentsExecutionConfigurationGatherer();

        // Act
        await gatherer.GatherAsync(context, resource, NullLogger.Instance, builder.ExecutionContext);
        // Assert
        Assert.Single(context.Arguments);
        Assert.Equal("async-arg", context.Arguments[0]);
    }

    #endregion

    #region EnvironmentVariablesExecutionConfigurationGatherer Tests

    [Fact]
    public async Task EnvironmentVariablesExecutionConfigurationGatherer_WithEnvironmentCallback_GathersEnvironmentVariables()
    {
        // Arrange
        using var builder = TestDistributedApplicationBuilder.Create();
        var resource = builder.AddContainer("test", "image")
            .WithEnvironment("KEY1", "value1")
            .WithEnvironment("KEY2", "value2")
            .Resource;

        await builder.BuildAsync();

        var context = new ExecutionConfigurationGathererContext();
        var gatherer = new EnvironmentVariablesExecutionConfigurationGatherer();

        // Act
        await gatherer.GatherAsync(context, resource, NullLogger.Instance, builder.ExecutionContext);
        // Assert
        Assert.Equal(2, context.EnvironmentVariables.Count);
        Assert.Equal("value1", context.EnvironmentVariables["KEY1"]);
        Assert.Equal("value2", context.EnvironmentVariables["KEY2"]);
    }

    [Fact]
    public async Task EnvironmentVariablesExecutionConfigurationGatherer_WithMultipleCallbacks_GathersAllVariables()
    {
        // Arrange
        using var builder = TestDistributedApplicationBuilder.Create();
        var resource = builder.AddContainer("test", "image")
            .WithEnvironment("KEY1", "value1")
            .WithEnvironment(ctx => ctx.EnvironmentVariables["KEY2"] = "value2")
            .Resource;

        await builder.BuildAsync();

        var context = new ExecutionConfigurationGathererContext();
        var gatherer = new EnvironmentVariablesExecutionConfigurationGatherer();

        // Act
        await gatherer.GatherAsync(context, resource, NullLogger.Instance, builder.ExecutionContext);
        // Assert
        Assert.Equal(2, context.EnvironmentVariables.Count);
        Assert.Equal("value1", context.EnvironmentVariables["KEY1"]);
        Assert.Equal("value2", context.EnvironmentVariables["KEY2"]);
    }

    [Fact]
    public async Task EnvironmentVariablesExecutionConfigurationGatherer_NoEnvironmentAnnotations_DoesNothing()
    {
        // Arrange
        using var builder = TestDistributedApplicationBuilder.Create();
        var resource = builder.AddContainer("test", "image").Resource;

        await builder.BuildAsync();

        var context = new ExecutionConfigurationGathererContext();
        var gatherer = new EnvironmentVariablesExecutionConfigurationGatherer();

        // Act
        await gatherer.GatherAsync(context, resource, NullLogger.Instance, builder.ExecutionContext);
        // Assert
        Assert.Empty(context.EnvironmentVariables);
    }

    [Fact]
    public async Task EnvironmentVariablesExecutionConfigurationGatherer_AsyncCallback_ExecutesCorrectly()
    {
        // Arrange
        using var builder = TestDistributedApplicationBuilder.Create();
        var resource = builder.AddContainer("test", "image")
            .WithEnvironment(async ctx =>
            {
                await Task.Delay(1);
                ctx.EnvironmentVariables["ASYNC_KEY"] = "async-value";
            })
            .Resource;

        await builder.BuildAsync();

        var context = new ExecutionConfigurationGathererContext();
        var gatherer = new EnvironmentVariablesExecutionConfigurationGatherer();

        // Act
        await gatherer.GatherAsync(context, resource, NullLogger.Instance, builder.ExecutionContext);

        // Assert
        Assert.Single(context.EnvironmentVariables);
        Assert.Equal("async-value", context.EnvironmentVariables["ASYNC_KEY"]);
    }

    #endregion

    #region CertificateTrustExecutionConfigurationGatherer Tests

    [Fact]
    public async Task CertificateTrustExecutionConfigurationGatherer_WithCertificateAuthorityCollection_SetsEnvironmentVariables()
    {
        // Arrange
        using var builder = TestDistributedApplicationBuilder.Create();
        var cert = CreateTestCertificate();
        var caCollection = builder.AddCertificateAuthorityCollection("test-ca").WithCertificate(cert);

        var resource = builder.AddContainer("test", "image")
            .WithCertificateAuthorityCollection(caCollection)
            .Resource;

        await builder.BuildAsync();

        var configContextFactory = CreateCertificateTrustConfigurationContextFactory();
        var context = new ExecutionConfigurationGathererContext();
        var gatherer = new CertificateTrustExecutionConfigurationGatherer(configContextFactory);

        // Act
        await gatherer.GatherAsync(context, resource, NullLogger.Instance, builder.ExecutionContext);
        // Assert
        Assert.Contains("SSL_CERT_DIR", context.EnvironmentVariables.Keys);
        var metadata = context.AdditionalConfigurationData.OfType<CertificateTrustExecutionConfigurationData>().Single();
        Assert.Equal(CertificateTrustScope.Append, metadata.Scope);
        Assert.NotEmpty(metadata.Certificates);
    }

    [Fact]
    public async Task CertificateTrustExecutionConfigurationGatherer_WithSystemScope_IncludesSystemCertificates()
    {
        // Arrange
        using var builder = TestDistributedApplicationBuilder.Create();
        var cert = CreateTestCertificate();
        var caCollection = builder.AddCertificateAuthorityCollection("test-ca").WithCertificate(cert);

        var resource = builder.AddContainer("test", "image")
            .WithCertificateAuthorityCollection(caCollection)
            .WithCertificateTrustScope(CertificateTrustScope.System)
            .Resource;

        await builder.BuildAsync();

        var configContextFactory = CreateCertificateTrustConfigurationContextFactory();
        var context = new ExecutionConfigurationGathererContext();
        var gatherer = new CertificateTrustExecutionConfigurationGatherer(configContextFactory);

        // Act
        await gatherer.GatherAsync(context, resource, NullLogger.Instance, builder.ExecutionContext);
        // Assert
        Assert.Contains("SSL_CERT_FILE", context.EnvironmentVariables.Keys);
        Assert.Contains("SSL_CERT_DIR", context.EnvironmentVariables.Keys);
        var metadata = context.AdditionalConfigurationData.OfType<CertificateTrustExecutionConfigurationData>().Single();
        Assert.Equal(CertificateTrustScope.System, metadata.Scope);
        // System scope should include system root certificates
        Assert.True(metadata.Certificates.Count > 1);
    }

    [Fact]
    public async Task CertificateTrustExecutionConfigurationGatherer_WithOverrideScope_SetsCorrectEnvironmentVariables()
    {
        // Arrange
        using var builder = TestDistributedApplicationBuilder.Create();
        var cert = CreateTestCertificate();
        var caCollection = builder.AddCertificateAuthorityCollection("test-ca").WithCertificate(cert);

        var resource = builder.AddContainer("test", "image")
            .WithCertificateAuthorityCollection(caCollection)
            .WithCertificateTrustScope(CertificateTrustScope.Override)
            .Resource;

        await builder.BuildAsync();

        var configContextFactory = CreateCertificateTrustConfigurationContextFactory();
        var context = new ExecutionConfigurationGathererContext();
        var gatherer = new CertificateTrustExecutionConfigurationGatherer(configContextFactory);

        // Act
        await gatherer.GatherAsync(context, resource, NullLogger.Instance, builder.ExecutionContext);
        // Assert
        Assert.Contains("SSL_CERT_FILE", context.EnvironmentVariables.Keys);
        Assert.Contains("SSL_CERT_DIR", context.EnvironmentVariables.Keys);
        var metadata = context.AdditionalConfigurationData.OfType<CertificateTrustExecutionConfigurationData>().Single();
        Assert.Equal(CertificateTrustScope.Override, metadata.Scope);
    }

    [Fact]
    public async Task CertificateTrustExecutionConfigurationGatherer_WithNoneScope_DoesNotSetEnvironmentVariables()
    {
        // Arrange
        using var builder = TestDistributedApplicationBuilder.Create();
        var cert = CreateTestCertificate();
        var caCollection = builder.AddCertificateAuthorityCollection("test-ca").WithCertificate(cert);

        var resource = builder.AddContainer("test", "image")
            .WithCertificateAuthorityCollection(caCollection)
            .WithCertificateTrustScope(CertificateTrustScope.None)
            .Resource;

        await builder.BuildAsync();

        var configContextFactory = CreateCertificateTrustConfigurationContextFactory();
        var context = new ExecutionConfigurationGathererContext();
        var gatherer = new CertificateTrustExecutionConfigurationGatherer(configContextFactory);

        // Act
        await gatherer.GatherAsync(context, resource, NullLogger.Instance, builder.ExecutionContext);
        // Assert
        Assert.DoesNotContain("SSL_CERT_FILE", context.EnvironmentVariables.Keys);
        Assert.DoesNotContain("SSL_CERT_DIR", context.EnvironmentVariables.Keys);
        var metadata = context.AdditionalConfigurationData.OfType<CertificateTrustExecutionConfigurationData>().Single();
        Assert.Equal(CertificateTrustScope.None, metadata.Scope);
    }

    [Fact]
    public async Task CertificateTrustExecutionConfigurationGatherer_NoCertificateAnnotation_DoesNothing()
    {
        // Arrange
        using var builder = TestDistributedApplicationBuilder.Create();
        var resource = builder.AddContainer("test", "image").Resource;

        await builder.BuildAsync();

        var configContextFactory = CreateCertificateTrustConfigurationContextFactory();
        var context = new ExecutionConfigurationGathererContext();
        var gatherer = new CertificateTrustExecutionConfigurationGatherer(configContextFactory);

        // Act
        await gatherer.GatherAsync(context, resource, NullLogger.Instance, builder.ExecutionContext);
        // Assert
        Assert.DoesNotContain("SSL_CERT_FILE", context.EnvironmentVariables.Keys);
        Assert.DoesNotContain("SSL_CERT_DIR", context.EnvironmentVariables.Keys);
    }

    [Fact]
    public async Task CertificateTrustExecutionConfigurationGatherer_WithAppendScope_DoesNotSetSSL_CERT_FILE()
    {
        // Arrange
        using var builder = TestDistributedApplicationBuilder.Create();
        var cert = CreateTestCertificate();
        var caCollection = builder.AddCertificateAuthorityCollection("test-ca").WithCertificate(cert);

        var resource = builder.AddContainer("test", "image")
            .WithCertificateAuthorityCollection(caCollection)
            .WithCertificateTrustScope(CertificateTrustScope.Append)
            .Resource;

        await builder.BuildAsync();

        var configContextFactory = CreateCertificateTrustConfigurationContextFactory();
        var context = new ExecutionConfigurationGathererContext();
        var gatherer = new CertificateTrustExecutionConfigurationGatherer(configContextFactory);

        // Act
        await gatherer.GatherAsync(context, resource, NullLogger.Instance, builder.ExecutionContext);
        // Assert
        Assert.DoesNotContain("SSL_CERT_FILE", context.EnvironmentVariables.Keys);
        Assert.Contains("SSL_CERT_DIR", context.EnvironmentVariables.Keys);
    }

    #endregion

    #region CreateCustomBundle Tests

    [Fact]
    public async Task CreateCustomBundle_RegistersBundleFactory()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var cert = CreateTestCertificate();
        var caCollection = builder.AddCertificateAuthorityCollection("test-ca").WithCertificate(cert);

        var resource = builder.AddContainer("test", "image")
            .WithCertificateAuthorityCollection(caCollection)
            .WithCertificateTrustConfiguration(ctx =>
            {
                var bundlePath = ctx.CreateCustomBundle((certs, ct) =>
                    Task.FromResult(new byte[] { 1, 2, 3 }));
                ctx.EnvironmentVariables["CUSTOM_BUNDLE"] = bundlePath;
                return Task.CompletedTask;
            })
            .Resource;

        await builder.BuildAsync();

        var configContextFactory = CreateCertificateTrustConfigurationContextFactory();
        var context = new ExecutionConfigurationGathererContext();
        var gatherer = new CertificateTrustExecutionConfigurationGatherer(configContextFactory);

        await gatherer.GatherAsync(context, resource, NullLogger.Instance, builder.ExecutionContext);

        var metadata = context.AdditionalConfigurationData.OfType<CertificateTrustExecutionConfigurationData>().Single();
        Assert.Single(metadata.CustomBundlesFactories);
    }

    [Fact]
    public async Task CreateCustomBundle_ReturnsReferenceExpressionWithBundlePath()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var cert = CreateTestCertificate();
        var caCollection = builder.AddCertificateAuthorityCollection("test-ca").WithCertificate(cert);

        ReferenceExpression? capturedBundlePath = null;
        var resource = builder.AddContainer("test", "image")
            .WithCertificateAuthorityCollection(caCollection)
            .WithCertificateTrustConfiguration(ctx =>
            {
                capturedBundlePath = ctx.CreateCustomBundle((certs, ct) =>
                    Task.FromResult(new byte[] { 1, 2, 3 }));
                return Task.CompletedTask;
            })
            .Resource;

        await builder.BuildAsync();

        var configContextFactory = CreateCertificateTrustConfigurationContextFactory();
        var context = new ExecutionConfigurationGathererContext();
        var gatherer = new CertificateTrustExecutionConfigurationGatherer(configContextFactory);

        await gatherer.GatherAsync(context, resource, NullLogger.Instance, builder.ExecutionContext);

        Assert.NotNull(capturedBundlePath);
        var resolvedPath = await capturedBundlePath.GetValueAsync(CancellationToken.None);
        Assert.NotNull(resolvedPath);
        Assert.Contains("/bundles/", resolvedPath);
        Assert.StartsWith("/etc/ssl/certs", resolvedPath);
    }

    [Fact]
    public async Task CreateCustomBundle_MultipleBundles_AllRegistered()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var cert = CreateTestCertificate();
        var caCollection = builder.AddCertificateAuthorityCollection("test-ca").WithCertificate(cert);

        var resource = builder.AddContainer("test", "image")
            .WithCertificateAuthorityCollection(caCollection)
            .WithCertificateTrustConfiguration(ctx =>
            {
                var bundle1 = ctx.CreateCustomBundle((certs, ct) =>
                    Task.FromResult(new byte[] { 1 }));
                var bundle2 = ctx.CreateCustomBundle((certs, ct) =>
                    Task.FromResult(new byte[] { 2 }));
                ctx.EnvironmentVariables["BUNDLE1"] = bundle1;
                ctx.EnvironmentVariables["BUNDLE2"] = bundle2;
                return Task.CompletedTask;
            })
            .Resource;

        await builder.BuildAsync();

        var configContextFactory = CreateCertificateTrustConfigurationContextFactory();
        var context = new ExecutionConfigurationGathererContext();
        var gatherer = new CertificateTrustExecutionConfigurationGatherer(configContextFactory);

        await gatherer.GatherAsync(context, resource, NullLogger.Instance, builder.ExecutionContext);

        var metadata = context.AdditionalConfigurationData.OfType<CertificateTrustExecutionConfigurationData>().Single();
        Assert.Equal(2, metadata.CustomBundlesFactories.Count);

        // Verify each factory has a distinct key
        var keys = metadata.CustomBundlesFactories.Keys.ToList();
        Assert.NotEqual(keys[0], keys[1]);
    }

    [Fact]
    public async Task CreateCustomBundle_Pkcs12BundleFactory_ProducesValidPkcs12()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var cert = CreateTestCertificate();
        var caCollection = builder.AddCertificateAuthorityCollection("test-ca").WithCertificate(cert);

        var resource = builder.AddContainer("test", "image")
            .WithCertificateAuthorityCollection(caCollection)
            .WithCertificateTrustConfiguration(ctx =>
            {
                var password = string.Empty;
                var bundlePath = ctx.CreateCustomBundle((certificates, ct) =>
                {
                    var pkcs12Builder = new Pkcs12Builder();
                    var safeContents = new Pkcs12SafeContents();

                    // Oracle/Java trust anchor bag attribute OID
                    var trustAnchorOid = new Oid("2.16.840.1.113894.746875.1.1");
                    var asnWriter = new AsnWriter(AsnEncodingRules.DER);
                    asnWriter.WriteObjectIdentifier("2.5.29.37.0");
                    var trustAnchorValue = asnWriter.Encode();

                    for (var i = 0; i < certificates.Count; i++)
                    {
                        var publicCert = new X509Certificate2(certificates[i].Export(X509ContentType.Cert));
                        var certBag = safeContents.AddCertificate(publicCert);
                        certBag.Attributes.Add(
                            new CryptographicAttributeObject(
                                trustAnchorOid,
                                new AsnEncodedDataCollection(new AsnEncodedData(trustAnchorOid, trustAnchorValue))));
                    }

                    pkcs12Builder.AddSafeContentsUnencrypted(safeContents);
                    pkcs12Builder.SealWithMac(password, HashAlgorithmName.SHA256, iterationCount: 2048);

                    return Task.FromResult(pkcs12Builder.Encode());
                });
                ctx.EnvironmentVariables["JAVAX_NET_SSL_TRUSTSTORE"] = bundlePath;
                ctx.EnvironmentVariables["JAVAX_NET_SSL_TRUSTSTOREPASSWORD"] = password;
                return Task.CompletedTask;
            })
            .Resource;

        await builder.BuildAsync();

        var configContextFactory = CreateCertificateTrustConfigurationContextFactory();
        var context = new ExecutionConfigurationGathererContext();
        var gatherer = new CertificateTrustExecutionConfigurationGatherer(configContextFactory);

        await gatherer.GatherAsync(context, resource, NullLogger.Instance, builder.ExecutionContext);

        var metadata = context.AdditionalConfigurationData.OfType<CertificateTrustExecutionConfigurationData>().Single();
        Assert.Single(metadata.CustomBundlesFactories);

        // Invoke the stored factory and validate the PKCS#12 output
        var factory = metadata.CustomBundlesFactories.Values.Single();
        var pkcs12Bytes = await factory(metadata.Certificates, CancellationToken.None);
        Assert.NotEmpty(pkcs12Bytes);

        var loaded = new X509Certificate2Collection();
        loaded.Import(pkcs12Bytes, string.Empty, X509KeyStorageFlags.DefaultKeySet);
        Assert.Single(loaded);
        Assert.Equal(cert.Thumbprint, loaded[0].Thumbprint);
        Assert.False(loaded[0].HasPrivateKey);

        // Verify the PKCS#12 contains Java trust anchor attributes
        var info = Pkcs12Info.Decode(pkcs12Bytes, out _, skipCopy: true);
        var bagsWithTrustAnchor = 0;
        foreach (var safeContents in info.AuthenticatedSafe)
        {
            foreach (var bag in safeContents.GetBags())
            {
                foreach (CryptographicAttributeObject attr in bag.Attributes)
                {
                    if (attr.Oid.Value == "2.16.840.1.113894.746875.1.1")
                    {
                        Assert.Single(attr.Values);
                        var reader = new AsnReader(attr.Values[0].RawData, AsnEncodingRules.DER);
                        Assert.Equal("2.5.29.37.0", reader.ReadObjectIdentifier());
                        Assert.False(reader.HasData);
                        bagsWithTrustAnchor++;
                        break;
                    }
                }
            }
        }
        Assert.Equal(1, bagsWithTrustAnchor);
    }

    [Fact]
    public async Task CreateCustomBundle_FactoryReceivesCertificatesAndCancellationToken()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var cert = CreateTestCertificate();
        var caCollection = builder.AddCertificateAuthorityCollection("test-ca").WithCertificate(cert);

        var resource = builder.AddContainer("test", "image")
            .WithCertificateAuthorityCollection(caCollection)
            .WithCertificateTrustConfiguration(ctx =>
            {
                ctx.CreateCustomBundle((certs, ct) =>
                    Task.FromResult(Array.Empty<byte>()));
                return Task.CompletedTask;
            })
            .Resource;

        await builder.BuildAsync();

        var configContextFactory = CreateCertificateTrustConfigurationContextFactory();
        var context = new ExecutionConfigurationGathererContext();
        var gatherer = new CertificateTrustExecutionConfigurationGatherer(configContextFactory);

        await gatherer.GatherAsync(context, resource, NullLogger.Instance, builder.ExecutionContext);

        var metadata = context.AdditionalConfigurationData.OfType<CertificateTrustExecutionConfigurationData>().Single();
        var factory = metadata.CustomBundlesFactories.Values.Single();

        // Invoke the factory with the certificates from the metadata and verify it receives them
        using var cts = new CancellationTokenSource();
        var result = await factory(metadata.Certificates, cts.Token);
        Assert.NotNull(result);
        Assert.Single(metadata.Certificates);
        Assert.Equal(cert.Thumbprint, metadata.Certificates[0].Thumbprint);
    }

    #endregion

    #region HttpsCertificateExecutionConfigurationGatherer Tests

    [Fact]
    public async Task HttpsCertificateExecutionConfigurationGatherer_WithCertificate_ConfiguresMetadata()
    {
        // Arrange
        using var builder = TestDistributedApplicationBuilder.Create();
        var cert = CreateTestCertificateWithPrivateKey();

        var resource = builder.AddContainer("test", "image")
            .WithAnnotation(new HttpsCertificateAnnotation { Certificate = cert })
            .Resource;

        await builder.BuildAsync();

        var configContextFactory = CreateHttpsCertificateConfigurationContextFactory();
        var context = new ExecutionConfigurationGathererContext();
        var gatherer = new HttpsCertificateExecutionConfigurationGatherer(configContextFactory);

        // Act
        await gatherer.GatherAsync(context, resource, NullLogger.Instance, builder.ExecutionContext);

        // Assert
        var metadata = context.AdditionalConfigurationData.OfType<HttpsCertificateExecutionConfigurationData>().Single();
        Assert.Equal(cert, metadata.Certificate);
        Assert.NotNull(metadata.KeyPathReference);
        Assert.NotNull(metadata.PfxPathReference);
    }

    [Fact]
    public async Task HttpsCertificateExecutionConfigurationGatherer_WithPassword_StoresPassword()
    {
        // Arrange
        using var builder = TestDistributedApplicationBuilder.Create();
        builder.Configuration["Parameters:password"] = "test-password";
        var cert = CreateTestCertificateWithPrivateKey();
        var password = builder.AddParameter("password", secret: true);

        var resource = builder.AddContainer("test", "image")
            .WithAnnotation(new HttpsCertificateAnnotation
            {
                Certificate = cert,
                Password = password.Resource
            })
            .Resource;

        await builder.BuildAsync();

        var configContextFactory = CreateHttpsCertificateConfigurationContextFactory();
        var context = new ExecutionConfigurationGathererContext();
        var gatherer = new HttpsCertificateExecutionConfigurationGatherer(configContextFactory);

        // Act
        await gatherer.GatherAsync(context, resource, NullLogger.Instance, builder.ExecutionContext);
        // Assert
        var metadata = context.AdditionalConfigurationData.OfType<HttpsCertificateExecutionConfigurationData>().Single();
        Assert.NotNull(metadata.Password);
    }

    [Fact]
    public async Task HttpsCertificateExecutionConfigurationGatherer_WithUseDeveloperCertificate_UsesDeveloperCert()
    {
        // Arrange
        using var builder = TestDistributedApplicationBuilder.Create();

        // Configure developer certificate service
        var devCert = CreateTestCertificate();
        builder.Services.AddSingleton<IDeveloperCertificateService>(new TestDeveloperCertificateService(devCert));

        var resource = builder.AddContainer("test", "image")
            .WithAnnotation(new HttpsCertificateAnnotation
            {
                UseDeveloperCertificate = true
            })
            .Resource;

        await builder.BuildAsync();

        var configContextFactory = CreateHttpsCertificateConfigurationContextFactory();
        var context = new ExecutionConfigurationGathererContext();
        var gatherer = new HttpsCertificateExecutionConfigurationGatherer(configContextFactory);

        // Act
        await gatherer.GatherAsync(context, resource, NullLogger.Instance, builder.ExecutionContext);
        // Assert
        var metadata = context.AdditionalConfigurationData.OfType<HttpsCertificateExecutionConfigurationData>().Single();
        Assert.Equal(devCert, metadata.Certificate);
    }

    [Fact]
    public async Task HttpsCertificateExecutionConfigurationGatherer_NoCertificateAnnotation_DoesNothing()
    {
        // Arrange
        using var builder = TestDistributedApplicationBuilder.Create();
        var resource = builder.AddContainer("test", "image").Resource;

        await builder.BuildAsync();

        var configContextFactory = CreateHttpsCertificateConfigurationContextFactory();
        var context = new ExecutionConfigurationGathererContext();
        var gatherer = new HttpsCertificateExecutionConfigurationGatherer(configContextFactory);

        // Act
        await gatherer.GatherAsync(context, resource, NullLogger.Instance, builder.ExecutionContext);
        // Assert
        Assert.Empty(context.AdditionalConfigurationData.OfType<HttpsCertificateExecutionConfigurationData>());
    }

    [Fact]
    public async Task HttpsCertificateExecutionConfigurationGatherer_TracksReferenceUsage()
    {
        // Arrange
        using var builder = TestDistributedApplicationBuilder.Create();
        var cert = CreateTestCertificateWithPrivateKey();

        var resource = builder.AddContainer("test", "image")
            .WithAnnotation(new HttpsCertificateAnnotation { Certificate = cert })
            .Resource;

        await builder.BuildAsync();

        var configContextFactory = CreateHttpsCertificateConfigurationContextFactory();
        var context = new ExecutionConfigurationGathererContext();
        var gatherer = new HttpsCertificateExecutionConfigurationGatherer(configContextFactory);

        // Act
        await gatherer.GatherAsync(context, resource, NullLogger.Instance, builder.ExecutionContext);
        // Assert
        var metadata = context.AdditionalConfigurationData.OfType<HttpsCertificateExecutionConfigurationData>().Single();

        // Initially, references should not be resolved
        Assert.False(metadata.IsKeyPathReferenced);
        Assert.False(metadata.IsPfxPathReferenced);

        // Accessing the references should mark them as resolved
        _ = await metadata.KeyPathReference.GetValueAsync(CancellationToken.None);
        Assert.True(metadata.IsKeyPathReferenced);
        Assert.False(metadata.IsPfxPathReferenced);

        _ = await metadata.PfxPathReference.GetValueAsync(CancellationToken.None);
        Assert.True(metadata.IsPfxPathReferenced);
    }

    [Fact]
    public async Task HttpsCertificateExecutionConfigurationGatherer_WithCallback_ExecutesCallback()
    {
        // Arrange
        using var builder = TestDistributedApplicationBuilder.Create();
        var cert = CreateTestCertificateWithPrivateKey();
        var callbackExecuted = false;

        var resource = builder.AddContainer("test", "image")
            .WithAnnotation(new HttpsCertificateAnnotation { Certificate = cert })
            .WithAnnotation(new HttpsCertificateConfigurationCallbackAnnotation(ctx =>
            {
                callbackExecuted = true;
                return Task.CompletedTask;
            }))
            .Resource;

        await builder.BuildAsync();

        var configContextFactory = CreateHttpsCertificateConfigurationContextFactory();
        var gatherer = new HttpsCertificateExecutionConfigurationGatherer(configContextFactory);
        var context = new ExecutionConfigurationGathererContext();

        // Act
        await gatherer.GatherAsync(context, resource, NullLogger.Instance, builder.ExecutionContext);

        // Assert
        Assert.True(callbackExecuted);
    }

    #endregion

    private static X509Certificate2 CreateTestCertificate()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            new X500DistinguishedName("CN=test"),
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        // Use Create() instead of CreateSelfSigned() to produce a public-only certificate
        // that doesn't touch the macOS keychain (avoids AppleCommonCryptoCryptographicException).
        var serialNumber = new byte[16];
        RandomNumberGenerator.Fill(serialNumber);
        var generator = X509SignatureGenerator.CreateForRSA(rsa, RSASignaturePadding.Pkcs1);

        return request.Create(
            request.SubjectName,
            generator,
            DateTimeOffset.Now,
            DateTimeOffset.Now.AddYears(1),
            serialNumber);
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

    private static Func<CertificateTrustScope, CertificateTrustExecutionConfigurationContext> CreateCertificateTrustConfigurationContextFactory()
    {
        return scope => new CertificateTrustExecutionConfigurationContext
        {
            CertificateBundlePath = ReferenceExpression.Create($"/etc/ssl/certs/ca-bundle.crt"),
            CertificateDirectoriesPath = ReferenceExpression.Create($"/etc/ssl/certs"),
            RootCertificatesPath = "/etc/ssl/certs",
            IsContainer = true,
        };
    }

    private static Func<X509Certificate2, HttpsCertificateExecutionConfigurationContext> CreateHttpsCertificateConfigurationContextFactory()
    {
        return cert => new HttpsCertificateExecutionConfigurationContext
        {
            CertificatePath = ReferenceExpression.Create($"/etc/ssl/certs/server.crt"),
            KeyPath = ReferenceExpression.Create($"/etc/ssl/private/server.key"),
            PfxPath = ReferenceExpression.Create($"/etc/ssl/certs/server.pfx")
        };
    }

    private sealed class TestDeveloperCertificateService : IDeveloperCertificateService
    {
        private readonly X509Certificate2? _certificate;

        public TestDeveloperCertificateService(X509Certificate2? certificate = null)
        {
            _certificate = certificate;
        }

        public ImmutableList<X509Certificate2> Certificates =>
            _certificate != null ? [_certificate] : ImmutableList<X509Certificate2>.Empty;

        public bool SupportsContainerTrust => true;

        public bool TrustCertificate => true;

        public bool UseForHttps => true;
    }
}
