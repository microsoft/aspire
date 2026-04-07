// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Utils;

namespace Aspire.Hosting.Tests;

[Trait("Partition", "5")]
public class WithCertificateAuthorityCollectionTests
{
    [Fact]
    public Task AddingCertificateAuthorityCollectionsIsIdempotent()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var bundle1 = builder.AddCertificateAuthorityCollection("bundle1");
        var bundle2 = builder.AddCertificateAuthorityCollection("bundle2");

        var container = builder.AddContainer("container", "image")
                               .WithEnvironment(context =>
                               {
                                   Assert.NotNull(context.Resource);

                                   var sp = context.ExecutionContext.ServiceProvider;
                                   context.EnvironmentVariables["SP_AVAILABLE"] = sp is not null ? "true" : "false";
                               })
                               .WithCertificateAuthorityCollection(bundle1)
                               .WithCertificateAuthorityCollection(bundle2)
                               .WithCertificateAuthorityCollection(bundle1); // Add bundle1 again to test idempotency

        Assert.True(container.Resource.TryGetAnnotationsOfType<CertificateAuthorityCollectionAnnotation>(out var annotations));
        var annotation = Assert.Single(annotations);
        Assert.Equal(2, annotation.CertificateAuthorityCollections.Count);
        Assert.Contains(bundle1.Resource, annotation.CertificateAuthorityCollections);
        Assert.Contains(bundle2.Resource, annotation.CertificateAuthorityCollections);

        return Task.CompletedTask;
    }
}
