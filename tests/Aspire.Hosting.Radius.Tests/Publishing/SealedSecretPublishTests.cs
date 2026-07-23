// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIRERADIUS006 // Experimental: the secret-store APIs are under test.
#pragma warning disable ASPIREPIPELINES001

using System.Reflection;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Radius.Publishing;
using Aspire.Hosting.Radius.Secrets;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Hosting.Radius.Tests.Publishing;

public class SealedSecretPublishTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("sealed-secret-tests").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string WriteManifest(string name, string ns)
    {
        var path = Path.Combine(_dir, $"{name}.sealed.yaml");
        File.WriteAllText(path,
            "apiVersion: bitnami.com/v1alpha1\n" +
            "kind: SealedSecret\n" +
            "metadata:\n" +
            $"  name: {name}\n" +
            $"  namespace: {ns}\n" +
            "spec:\n" +
            "  encryptedData:\n" +
            "    username: AgByCIPHERTEXTONLY\n");
        return path;
    }

    private static string GenerateStoreBicep(Action<IResourceBuilder<RadiusEnvironmentResource>> configure)
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var env = builder.AddRadiusEnvironment("radius");
        configure(env);

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var radiusEnv = model.Resources.OfType<RadiusEnvironmentResource>().First();
        RadiusTestHelper.AttachDeploymentTargets(radiusEnv, model);
        return new RadiusBicepPublishingContext(radiusEnv).GenerateBicep(model);
    }

    [Fact]
    public void WithSealedSecret_EmitsResourceReference_FromManifestMetadata_NoPlaintext()
    {
        var manifest = WriteManifest("db-creds", "app");

        var bicep = GenerateStoreBicep(env =>
            env.WithSecretStore("db-creds", RadiusSecretStoreType.BasicAuthentication, s =>
                s.WithSealedSecret(manifest, "username", "password")));

        Assert.Contains("Applications.Core/secretStores@2023-10-01-preview", bicep);
        Assert.Contains("resource: 'app/db-creds'", bicep);
        // The encrypted manifest is not inlined into the Bicep; no ciphertext or @secure() param.
        Assert.DoesNotContain("AgByCIPHERTEXTONLY", bicep);
        Assert.DoesNotContain("@secure()", bicep);
    }

    [Fact]
    public void WithSealedSecret_RelativePath_IsResolvedAgainstAppHostDirectory()
    {
        // A relative manifest path must be anchored to the AppHost directory (not the process working
        // directory) so `WithSealedSecret("./secrets/x.yaml", ...)` works no matter where the AppHost
        // process is launched from.
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var store = builder.AddRadiusSecretStore("db-creds", RadiusSecretStoreType.Generic)
            .WithSealedSecret(Path.Combine("secrets", "db-creds.sealed.yaml"), "username");

        var resolved = store.Resource.Population.SealedManifestPath;
        Assert.NotNull(resolved);
        Assert.True(Path.IsPathFullyQualified(resolved));
        Assert.Equal(
            Path.GetFullPath(Path.Combine("secrets", "db-creds.sealed.yaml"), store.ApplicationBuilder.AppHostDirectory),
            resolved);
    }

    [Fact]
    public void WithSealedSecret_MissingManifest_Throws_ASPIRERADIUS044()
    {
        var missing = Path.Combine(_dir, "does-not-exist.sealed.yaml");

        var ex = Assert.Throws<InvalidOperationException>(() =>
            GenerateStoreBicep(env =>
                env.WithSecretStore("db-creds", RadiusSecretStoreType.Generic, s =>
                    s.WithSealedSecret(missing, "key"))));

        Assert.Contains("ASPIRERADIUS044", ex.Message);
    }

    [Fact]
    public void WithSealedSecret_CopyWritesValidatedBytes_WhenSourceFileChangesAfterBuild()
    {
        var manifest = WriteManifest("db-creds", "app");
        var originalBytes = File.ReadAllBytes(manifest);

        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var env = builder.AddRadiusEnvironment("radius");
        env.WithSecretStore("db-creds", RadiusSecretStoreType.BasicAuthentication, s =>
            s.WithSealedSecret(manifest, "username", "password"));

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var radiusEnv = model.Resources.OfType<RadiusEnvironmentResource>().First();
        RadiusTestHelper.AttachDeploymentTargets(radiusEnv, model);
        var context = new RadiusBicepPublishingContext(radiusEnv);
        var options = context.BuildOptions(model);

        File.WriteAllText(manifest,
            "apiVersion: v1\n" +
            "kind: Secret\n" +
            "metadata:\n" +
            "  name: db-creds\n" +
            "data:\n" +
            "  username: dXNlcg==\n");

        var outputDir = Directory.CreateTempSubdirectory("sealed-secret-output").FullName;
        try
        {
            var copyMethod = typeof(RadiusBicepPublishingContext).GetMethod(
                "CopySealedSecretManifests",
                BindingFlags.NonPublic | BindingFlags.Static);

            Assert.NotNull(copyMethod);
            copyMethod.Invoke(null, [options, outputDir, NullLogger.Instance]);

            var destination = SealedSecretArtifact.ResolvePath(outputDir, "db-creds", manifest);
            Assert.Equal(originalBytes, File.ReadAllBytes(destination));
            Assert.NotEqual(File.ReadAllBytes(manifest), File.ReadAllBytes(destination));
        }
        finally
        {
            Directory.Delete(outputDir, recursive: true);
        }
    }
}
