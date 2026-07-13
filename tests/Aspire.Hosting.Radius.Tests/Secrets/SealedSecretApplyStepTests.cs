// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIRERADIUS006 // Experimental: the secret-store APIs are under test.

using Aspire.Hosting.Radius.Publishing;

namespace Aspire.Hosting.Radius.Tests.Secrets;

public class SealedSecretApplyStepTests
{
    [Fact]
    public void BuildApplyArgs_WithContext_PassesContextExplicitly()
    {
        var args = SealedSecretApplyStep.BuildApplyArgs("/p/db.sealed.yaml", "kind-radius");
        Assert.Equal(new[] { "apply", "-f", "/p/db.sealed.yaml", "--context", "kind-radius" }, args);
    }

    [Fact]
    public void BuildApplyArgs_NoContext_OmitsContextFlag()
    {
        var args = SealedSecretApplyStep.BuildApplyArgs("/p/db.sealed.yaml", null);
        Assert.Equal(new[] { "apply", "-f", "/p/db.sealed.yaml" }, args);
    }

    [Fact]
    public void BuildApplyArgs_WithNamespace_PassesNamespaceExplicitly()
    {
        var args = SealedSecretApplyStep.BuildApplyArgs("/p/db.sealed.yaml", "kind-radius", "app");
        Assert.Equal(new[] { "apply", "-f", "/p/db.sealed.yaml", "-n", "app", "--context", "kind-radius" }, args);
    }

    [Fact]
    public void BuildApplyArgs_NamespaceWithoutContext_PassesNamespaceOnly()
    {
        var args = SealedSecretApplyStep.BuildApplyArgs("/p/db.sealed.yaml", null, "app");
        Assert.Equal(new[] { "apply", "-f", "/p/db.sealed.yaml", "-n", "app" }, args);
    }

    [Fact]
    public void ParseActiveWorkspaceContext_SelectsDefaultWorkspaceContext()
    {
        // With multiple workspaces, the default workspace's context must be selected — not the
        // first `context:` in the file (which belongs to a non-default workspace here).
        var config =
            "workspaces:\n" +
            "  default: prod\n" +
            "  items:\n" +
            "    dev:\n" +
            "      connection:\n" +
            "        kind: kubernetes\n" +
            "        context: dev-cluster\n" +
            "    prod:\n" +
            "      connection:\n" +
            "        kind: kubernetes\n" +
            "        context: prod-cluster\n";

        Assert.Equal("prod-cluster", SealedSecretApplyStep.ParseActiveWorkspaceContext(config));
    }

    [Fact]
    public void ParseActiveWorkspaceContext_NoDefault_FallsBackToFirstContext()
    {
        var config =
            "workspaces:\n" +
            "  items:\n" +
            "    only:\n" +
            "      connection:\n" +
            "        context: only-cluster\n";

        Assert.Equal("only-cluster", SealedSecretApplyStep.ParseActiveWorkspaceContext(config));
    }

    [Fact]
    public void ParseActiveWorkspaceContext_NoContext_ReturnsNull()
    {
        var config =
            "workspaces:\n" +
            "  default: prod\n" +
            "  items:\n" +
            "    prod:\n" +
            "      connection:\n" +
            "        kind: kubernetes\n";

        Assert.Null(SealedSecretApplyStep.ParseActiveWorkspaceContext(config));
    }

    [Fact]
    public void BuildGetSecretArgs_TargetsNamespaceAndContext()
    {
        var args = SealedSecretApplyStep.BuildGetSecretArgs("app", "db-creds", "kind-radius");
        Assert.Equal(new[] { "get", "secret", "db-creds", "-n", "app", "-o", "name", "--context", "kind-radius" }, args);
    }

    [Fact]
    public async Task WaitForSecretMaterialization_ReturnsOnceSecretExists()
    {
        var calls = 0;
        await SealedSecretApplyStep.WaitForSecretMaterializationAsync(
            "db-creds", "app", "db-creds",
            timeout: TimeSpan.FromSeconds(5),
            interval: TimeSpan.FromMilliseconds(1),
            secretExists: _ => Task.FromResult(++calls >= 2),
            cancellationToken: default);

        Assert.True(calls >= 2);
    }

    [Fact]
    public async Task WaitForSecretMaterialization_TimesOut_Throws_ASPIRERADIUS046()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            SealedSecretApplyStep.WaitForSecretMaterializationAsync(
                "db-creds", "app", "db-creds",
                timeout: TimeSpan.FromMilliseconds(10),
                interval: TimeSpan.FromMilliseconds(1),
                secretExists: _ => Task.FromResult(false),
                cancellationToken: default));

        Assert.Contains("ASPIRERADIUS046", ex.Message);
        Assert.Contains("app/db-creds", ex.Message);
    }

    [Theory]
    [InlineData("Error from server (NotFound): secrets \"db-creds\" not found")]
    [InlineData("secrets \"db-creds\" not found")]
    public void IsNotFound_TreatsMissingSecretAsNotFound(string stderr)
    {
        Assert.True(SealedSecretApplyStep.IsNotFound(stderr, "db-creds"));
    }

    [Theory]
    [InlineData("Unable to connect to the server: dial tcp 127.0.0.1:6443: connect: connection refused")]
    [InlineData("error: You must be logged in to the server (Unauthorized)")]
    [InlineData("Error from server (Forbidden): secrets is forbidden")]
    [InlineData("exec: executable kubelogin not found")]
    [InlineData("Error from server (NotFound): namespaces \"app\" not found")]
    public void IsNotFound_TreatsRealFailuresAsNotNotFound(string stderr)
    {
        // A genuine kubectl failure will never resolve by polling, so it must not be treated as
        // "keep waiting" — SecretExistsAsync surfaces it instead of burning the whole timeout. This
        // includes a NotFound for a *different* resource (a missing namespace) and client errors that
        // merely contain the phrase "not found".
        Assert.False(SealedSecretApplyStep.IsNotFound(stderr, "db-creds"));
    }
}
