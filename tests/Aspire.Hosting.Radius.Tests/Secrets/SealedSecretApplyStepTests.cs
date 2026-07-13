// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIRERADIUS006 // Experimental: the secret-store APIs are under test.

using System.Text.Json;
using Aspire.Hosting.Radius.Publishing;

namespace Aspire.Hosting.Radius.Tests.Secrets;

public class SealedSecretApplyStepTests
{
    [Fact]
    public void BuildApplyArgs_WithContext_PassesContextExplicitly()
    {
        var args = SealedSecretApplyStep.BuildApplyArgs("/p/db.sealed.yaml", "kind-radius");
        Assert.Equal(new[] { "apply", "-f", "/p/db.sealed.yaml", "-o", "json", "--context", "kind-radius" }, args);
    }

    [Fact]
    public void BuildApplyArgs_NoContext_OmitsContextFlag()
    {
        var args = SealedSecretApplyStep.BuildApplyArgs("/p/db.sealed.yaml", null);
        Assert.Equal(new[] { "apply", "-f", "/p/db.sealed.yaml", "-o", "json" }, args);
    }

    [Fact]
    public void BuildApplyArgs_WithNamespace_PassesNamespaceExplicitly()
    {
        var args = SealedSecretApplyStep.BuildApplyArgs("/p/db.sealed.yaml", "kind-radius", "app");
        Assert.Equal(new[] { "apply", "-f", "/p/db.sealed.yaml", "-o", "json", "-n", "app", "--context", "kind-radius" }, args);
    }

    [Fact]
    public void BuildApplyArgs_NamespaceWithoutContext_PassesNamespaceOnly()
    {
        var args = SealedSecretApplyStep.BuildApplyArgs("/p/db.sealed.yaml", null, "app");
        Assert.Equal(new[] { "apply", "-f", "/p/db.sealed.yaml", "-o", "json", "-n", "app" }, args);
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
    public void ParseActiveWorkspaceContext_DefaultContextMissing_ReturnsNullInsteadOfFallbackContext()
    {
        var config =
            "workspaces:\n" +
            "  default: prod\n" +
            "  items:\n" +
            "    dev:\n" +
            "      connection:\n" +
            "        context: dev-cluster\n" +
            "    prod:\n" +
            "      connection:\n" +
            "        kind: kubernetes\n";

        Assert.Null(SealedSecretApplyStep.ParseActiveWorkspaceContext(config));
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
    public void BuildGetSealedSecretArgs_TargetsNamespaceAndContext()
    {
        var args = SealedSecretApplyStep.BuildGetSealedSecretArgs("app", "db-creds", "kind-radius");
        Assert.Equal(new[] { "get", "sealedsecret", "db-creds", "-n", "app", "-o", "json", "--context", "kind-radius" }, args);
    }

    [Fact]
    public async Task WaitForSealedSecretSynced_ReturnsOnceObservedGenerationMatchesSyncedTrueAndSecretExists()
    {
        var statusCalls = 0;
        var secretCalls = 0;
        await SealedSecretApplyStep.WaitForSealedSecretSyncedAsync(
            "db-creds", "app", "db-creds", appliedGeneration: 4,
            timeout: TimeSpan.FromSeconds(5),
            interval: TimeSpan.FromMilliseconds(1),
            getStatus: _ =>
            {
                statusCalls++;
                return Task.FromResult(statusCalls == 1
                    ? new SealedSecretApplyStep.SealedSecretStatusSnapshot(4, null, [])
                    : new SealedSecretApplyStep.SealedSecretStatusSnapshot(
                        4,
                        4,
                        [new SealedSecretApplyStep.SealedSecretCondition("Synced", "True", null)]));
            },
            secretExists: _ =>
            {
                secretCalls++;
                return Task.FromResult(secretCalls >= 2);
            },
            cancellationToken: default);

        Assert.True(statusCalls >= 3);
        Assert.True(secretCalls >= 2);
    }

    [Fact]
    public async Task WaitForSealedSecretSynced_FailsFastWhenSyncedFalseForAppliedGeneration()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            SealedSecretApplyStep.WaitForSealedSecretSyncedAsync(
                "db-creds", "app", "db-creds", appliedGeneration: 4,
                timeout: TimeSpan.FromSeconds(5),
                interval: TimeSpan.FromMilliseconds(1),
                getStatus: _ => Task.FromResult(new SealedSecretApplyStep.SealedSecretStatusSnapshot(
                    4,
                    4,
                    [new SealedSecretApplyStep.SealedSecretCondition("Synced", "False", "no key could decrypt secret")])),
                secretExists: _ => Task.FromResult(false),
                cancellationToken: default));

        Assert.Contains("ASPIRERADIUS058", ex.Message);
        Assert.Contains("no key could decrypt secret", ex.Message);
    }

    [Fact]
    public async Task WaitForSealedSecretSynced_TimesOutWhenStatusNeverMatches_Throws_ASPIRERADIUS058()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            SealedSecretApplyStep.WaitForSealedSecretSyncedAsync(
                "db-creds", "app", "db-creds", appliedGeneration: 4,
                timeout: TimeSpan.FromMilliseconds(10),
                interval: TimeSpan.FromMilliseconds(1),
                getStatus: _ => Task.FromResult(new SealedSecretApplyStep.SealedSecretStatusSnapshot(4, 3, [])),
                secretExists: _ => Task.FromResult(false),
                cancellationToken: default));

        Assert.Contains("ASPIRERADIUS058", ex.Message);
        Assert.Contains("--update-status=false", ex.Message);
        Assert.Contains("updateStatus: false", ex.Message);
        Assert.Contains("app/db-creds", ex.Message);
    }

    [Fact]
    public async Task WaitForSealedSecretSynced_FailsFastWhenGenerationAdvancesBeyondAppliedGeneration()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            SealedSecretApplyStep.WaitForSealedSecretSyncedAsync(
                "db-creds", "app", "db-creds", appliedGeneration: 4,
                timeout: TimeSpan.FromSeconds(5),
                interval: TimeSpan.FromMilliseconds(1),
                getStatus: _ => Task.FromResult(new SealedSecretApplyStep.SealedSecretStatusSnapshot(5, null, [])),
                secretExists: _ => Task.FromResult(false),
                cancellationToken: default));

        Assert.Contains("concurrent modification", ex.Message);
    }

    [Fact]
    public async Task WaitForSealedSecretSynced_StatusMatchesButSecretAbsent_KeepsWaitingThenTimesOut()
    {
        var secretCalls = 0;
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            SealedSecretApplyStep.WaitForSealedSecretSyncedAsync(
                "db-creds", "app", "db-creds", appliedGeneration: 4,
                timeout: TimeSpan.FromMilliseconds(10),
                interval: TimeSpan.FromMilliseconds(1),
                getStatus: _ => Task.FromResult(new SealedSecretApplyStep.SealedSecretStatusSnapshot(
                    4,
                    4,
                    [new SealedSecretApplyStep.SealedSecretCondition("Synced", "True", null)])),
                secretExists: _ =>
                {
                    secretCalls++;
                    return Task.FromResult(false);
                },
                cancellationToken: default));

        Assert.Contains("ASPIRERADIUS058", ex.Message);
        Assert.True(secretCalls > 1);
    }

    [Fact]
    public async Task WaitForSealedSecretSynced_HangingProbeTimesOutWith_ASPIRERADIUS058()
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            SealedSecretApplyStep.WaitForSealedSecretSyncedAsync(
                "db-creds", "app", "db-creds", appliedGeneration: 4,
                timeout: TimeSpan.FromMilliseconds(50),
                interval: TimeSpan.FromMilliseconds(1),
                getStatus: async ct =>
                {
                    await Task.Delay(Timeout.Infinite, ct);
                    return new SealedSecretApplyStep.SealedSecretStatusSnapshot(4, null, []);
                },
                secretExists: _ => Task.FromResult(false),
                cancellationToken: default));

        stopwatch.Stop();
        Assert.Contains("ASPIRERADIUS058", ex.Message);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task WaitForSealedSecretSynced_CancellationDuringPolling_ThrowsOperationCanceledException()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            SealedSecretApplyStep.WaitForSealedSecretSyncedAsync(
                "db-creds", "app", "db-creds", appliedGeneration: 4,
                timeout: TimeSpan.FromSeconds(5),
                interval: TimeSpan.FromMilliseconds(1),
                getStatus: _ => Task.FromResult(new SealedSecretApplyStep.SealedSecretStatusSnapshot(4, null, [])),
                secretExists: _ => Task.FromResult(false),
                cancellationToken: cts.Token));
    }

    [Fact]
    public void ParseGeneration_ReadsMetadataGenerationFromApplyJson()
    {
        var generation = SealedSecretApplyStep.ParseGeneration("""
            {
              "apiVersion": "bitnami.com/v1alpha1",
              "kind": "SealedSecret",
              "metadata": {
                "name": "db-creds",
                "generation": 7
              }
            }
            """);

        Assert.Equal(7, generation);
    }

    [Fact]
    public void ParseSealedSecretStatus_ReadsObservedGenerationAndConditions()
    {
        // Bitnami Sealed Secrets status is shaped like:
        //   status:
        //     observedGeneration: 4
        //     conditions:
        //     - type: Synced
        //       status: "True"
        //       message: SealedSecret unsealed successfully
        var status = SealedSecretApplyStep.ParseSealedSecretStatus("""
            {
              "apiVersion": "bitnami.com/v1alpha1",
              "kind": "SealedSecret",
              "metadata": {
                "name": "db-creds",
                "generation": 4
              },
              "status": {
                "observedGeneration": 4,
                "conditions": [
                  {
                    "type": "Ready",
                    "status": "True"
                  },
                  {
                    "type": "Synced",
                    "status": "True",
                    "message": "SealedSecret unsealed successfully"
                  }
                ]
              }
            }
            """);

        Assert.Equal(4, status.Generation);
        Assert.Equal(4, status.ObservedGeneration);
        Assert.Collection(
            status.Conditions,
            condition =>
            {
                Assert.Equal("Ready", condition.Type);
                Assert.Equal("True", condition.Status);
                Assert.Null(condition.Message);
            },
            condition =>
            {
                Assert.Equal("Synced", condition.Type);
                Assert.Equal("True", condition.Status);
                Assert.Equal("SealedSecret unsealed successfully", condition.Message);
            });
    }

    [Fact]
    public void ParseSealedSecretStatus_MissingStatus_ReturnsEmptyStatus()
    {
        var status = SealedSecretApplyStep.ParseSealedSecretStatus("""
            {
              "metadata": {
                "generation": 4
              }
            }
            """);

        Assert.Equal(4, status.Generation);
        Assert.Null(status.ObservedGeneration);
        Assert.Empty(status.Conditions);
    }

    [Fact]
    public void ParseSealedSecretStatus_MalformedJson_ThrowsJsonException()
    {
        Assert.ThrowsAny<JsonException>(() => SealedSecretApplyStep.ParseSealedSecretStatus("{"));
    }

    [Fact]
    public void EvaluateSealedSecretSync_MultipleConditions_UsesSyncedCondition()
    {
        var decision = SealedSecretApplyStep.EvaluateSealedSecretSync(
            new SealedSecretApplyStep.SealedSecretStatusSnapshot(
                4,
                4,
                [
                    new SealedSecretApplyStep.SealedSecretCondition("Ready", "False", "not ready"),
                    new SealedSecretApplyStep.SealedSecretCondition("Synced", "True", null),
                ]),
            appliedGeneration: 4);

        Assert.Equal(SealedSecretApplyStep.SealedSecretSyncDecisionKind.Synced, decision.Kind);
    }

    [Fact]
    public void RequireKubeContext_ReturnsOverrideWhenProvided()
    {
        var context = SealedSecretApplyStep.RequireKubeContext("ci-context", "workspace-context", "~/.rad/config.yaml");

        Assert.Equal("ci-context", context);
    }

    [Fact]
    public void RequireKubeContext_ReturnsParsedContextWhenOverrideAbsent()
    {
        var context = SealedSecretApplyStep.RequireKubeContext(null, "workspace-context", "~/.rad/config.yaml");

        Assert.Equal("workspace-context", context);
    }

    [Fact]
    public void RequireKubeContext_ThrowsWhenContextCannotBeResolved()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            SealedSecretApplyStep.RequireKubeContext(null, null, "~/.rad/config.yaml"));

        Assert.Contains("ASPIRERADIUS059", ex.Message);
        Assert.Contains("~/.rad/config.yaml", ex.Message);
        Assert.Contains("ASPIRE_RADIUS_KUBE_CONTEXT", ex.Message);
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
