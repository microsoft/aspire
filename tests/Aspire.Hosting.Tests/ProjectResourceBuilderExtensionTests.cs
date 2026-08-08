// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREPERSISTENCE001 // Resource lifetime APIs are experimental.
#pragma warning disable ASPIRECERTIFICATES001
#pragma warning disable ASPIREPROJECTS001 // WithProjectDefaults is experimental.
#pragma warning disable ASPIREEXTENSION001 // Debug support APIs are experimental.

using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Hosting.Tests;

[Trait("Partition", "2")]
public class ProjectResourceBuilderExtensionTests
{
    [Fact]
    public void ProjectLaunchDefaultsAnnotationIsTaggedWithExpectedExperimentalDiagnostic()
    {
        var attribute = Assert.Single(typeof(ProjectLaunchDefaultsAnnotation).GetCustomAttributes<ExperimentalAttribute>());

        Assert.Equal("ASPIREPROJECTS001", attribute.DiagnosticId);
        Assert.Equal("https://aka.ms/aspire/diagnostics/{0}", attribute.UrlFormat);
    }

    [Fact]
    public void WithProjectDefaultsIsTaggedWithExpectedExperimentalDiagnostic()
    {
        var method = typeof(ProjectResourceBuilderExtensions).GetMethod(
            nameof(ProjectResourceBuilderExtensions.WithProjectDefaults),
            BindingFlags.Public | BindingFlags.Static);

        Assert.NotNull(method);
        var attribute = Assert.Single(method!.GetCustomAttributes<ExperimentalAttribute>());

        Assert.Equal("ASPIREPROJECTS001", attribute.DiagnosticId);
        Assert.Equal("https://aka.ms/aspire/diagnostics/{0}", attribute.UrlFormat);
    }

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
    public void WithProjectDefaultsThrowsClearExceptionWhenResourceHasNoProjectMetadata()
    {
        // WithProjectDefaults is public and only constrained to IResourceWithEnvironment /
        // IResourceWithEndpoints / IResourceWithArgs, so nothing stops a caller from invoking it on a
        // resource that never received an IProjectMetadata annotation. Without this guard, the first
        // internal .Single() over that annotation type throws a generic, unhelpful
        // "Sequence contains no elements" exception instead.
        using var builder = TestDistributedApplicationBuilder.Create();

        var executable = builder.AddExecutable("exe", "dotnet", ".", "run");

        var exception = Assert.Throws<InvalidOperationException>(
            () => executable.WithProjectDefaults(new ProjectResourceOptions { ExcludeLaunchProfile = true }));

        Assert.Contains(nameof(IProjectMetadata), exception.Message);
        Assert.Contains(executable.Resource.Name, exception.Message);
    }

    [Theory]
    [InlineData(DistributedApplicationOperation.Run)]
    [InlineData(DistributedApplicationOperation.Publish)]
    public void WithProjectDefaultsThrowsWhenAppliedTwice(DistributedApplicationOperation operation)
    {
        // Now that WithProjectDefaults is public, a caller can reach it again on a resource that
        // AddProject already applied it to. Almost all of the wiring is append-only: in run mode the
        // second pass tries to add a second "{name}-rebuilder" resource, which fails on the duplicate
        // name, and in both modes the debug support and environment callbacks would be duplicated.
        using var builder = TestDistributedApplicationBuilder.Create(operation);

        var project = builder.AddProject<TestProject>("project", options => options.ExcludeLaunchProfile = true);

        var exception = Assert.Throws<InvalidOperationException>(
            () => project.WithProjectDefaults(new ProjectResourceOptions { LaunchProfileName = "https" }));

        Assert.Contains("already been applied", exception.Message);
        Assert.Contains(project.Resource.Name, exception.Message);
    }

    [Fact]
    public void WithProjectDefaultsAppliesToAProjectResourceThatWasAddedDirectly()
    {
        // ProjectResource adds ProjectLaunchDefaultsAnnotation in its constructor, so the "already
        // applied" guard must key off the flag on the annotation rather than its presence.
        using var builder = TestDistributedApplicationBuilder.Create();

        var project = builder.AddResource(new ProjectResource("project"))
                             .WithAnnotation<IProjectMetadata>(new TestProject())
                             .WithProjectDefaults(new ProjectResourceOptions { ExcludeLaunchProfile = true });

        Assert.Single(project.Resource.Annotations.OfType<ProjectLaunchDefaultsAnnotation>());
    }

    [Fact]
    public void ProjectMetadataTargetNameDefaultsToNullForImplementationsThatDoNotSupplyIt()
    {
        // TargetName is a default interface member so metadata types that shipped before the
        // build-time contract existed (external implementations, path-based and file-based apps)
        // stay source and binary compatible.
        IProjectMetadata metadata = new TestProject();

        Assert.Null(metadata.TargetName);
    }

    [Fact]
    public void ProjectMetadataTargetNameIsNullForPathBasedProjects()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var project = builder.AddProject("project", Path.Combine(AppContext.BaseDirectory, "project.csproj"), options => options.ExcludeLaunchProfile = true);

        Assert.Null(project.Resource.GetProjectMetadata().TargetName);
    }

    [Fact]
    public void WithProjectDefaultsThrowsWhenResourceHasMultipleProjectMetadataAnnotations()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var project = builder.AddResource(new ProjectResource("project"))
                             .WithAnnotation<IProjectMetadata>(new TestProject())
                             .WithAnnotation<IProjectMetadata>(new OverrideTestProject());

        var exception = Assert.Throws<InvalidOperationException>(
            () => project.WithProjectDefaults(new ProjectResourceOptions { ExcludeLaunchProfile = true }));

        Assert.Contains(project.Resource.Name, exception.Message);
        Assert.Contains("more than one", exception.Message);
    }

    [Theory]
    [InlineData(ResourceAnnotationMutationBehavior.Append, "more than one")]
    [InlineData(ResourceAnnotationMutationBehavior.Replace, "replaced")]
    public void ProjectMetadataConsumersRejectChangesAfterProjectDefaultsAreApplied(
        ResourceAnnotationMutationBehavior mutationBehavior,
        string expectedMessage)
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var project = builder.AddProject<TestProject>("project", options => options.ExcludeLaunchProfile = true);
        project.WithAnnotation<IProjectMetadata>(new OverrideTestProject(), mutationBehavior);

        var exception = Assert.Throws<InvalidOperationException>(project.Resource.GetProjectMetadata);

        Assert.Contains(project.Resource.Name, exception.Message);
        Assert.Contains(expectedMessage, exception.Message);
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

    private sealed class OverrideTestProject : IProjectMetadata
    {
        public string ProjectPath => "override.csproj";
    }
}
