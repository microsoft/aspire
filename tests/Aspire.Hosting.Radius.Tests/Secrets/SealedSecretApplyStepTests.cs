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
        var args = SealedSecretApplyStep.BuildApplyArgs("kind-radius");
        Assert.Equal(new[] { "apply", "-f", "-", "-o", "json", "--context", "kind-radius" }, args);
    }

    [Fact]
    public void BuildApplyArgs_NoContext_OmitsContextFlag()
    {
        var args = SealedSecretApplyStep.BuildApplyArgs(null);
        Assert.Equal(new[] { "apply", "-f", "-", "-o", "json" }, args);
    }

    [Fact]
    public void BuildApplyArgs_WithNamespace_PassesNamespaceExplicitly()
    {
        var args = SealedSecretApplyStep.BuildApplyArgs("kind-radius", "app");
        Assert.Equal(new[] { "apply", "-f", "-", "-o", "json", "-n", "app", "--context", "kind-radius" }, args);
    }

    [Fact]
    public void BuildApplyArgs_NamespaceWithoutContext_PassesNamespaceOnly()
    {
        var args = SealedSecretApplyStep.BuildApplyArgs(null, "app");
        Assert.Equal(new[] { "apply", "-f", "-", "-o", "json", "-n", "app" }, args);
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
    public void ParseActiveWorkspaceContext_NoDefault_SingleContext_ReturnsIt()
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
    public void ParseActiveWorkspaceContext_NoDefault_MultipleContexts_ReturnsNull()
    {
        // Without a `workspaces.default` selector there is no evidence which of several contexts is
        // active, so the parser must fail closed rather than guessing the first one and applying the
        // SealedSecret to the wrong cluster.
        var config =
            "workspaces:\n" +
            "  items:\n" +
            "    dev:\n" +
            "      connection:\n" +
            "        context: dev-cluster\n" +
            "    prod:\n" +
            "      connection:\n" +
            "        context: prod-cluster\n";

        Assert.Null(SealedSecretApplyStep.ParseActiveWorkspaceContext(config));
    }

    [Fact]
    public void ParseActiveWorkspaceContext_NoDefault_RepeatedIdenticalContext_ReturnsIt()
    {
        // The same context repeated (e.g. duplicated workspace pointing at one cluster) resolves to a
        // single distinct value, which is still unambiguous.
        var config =
            "workspaces:\n" +
            "  items:\n" +
            "    a:\n" +
            "      connection:\n" +
            "        context: shared\n" +
            "    b:\n" +
            "      connection:\n" +
            "        context: shared\n";

        Assert.Equal("shared", SealedSecretApplyStep.ParseActiveWorkspaceContext(config));
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
    public void ParseActiveWorkspaceContext_DefaultValueWithTrailingComment_IsHonored()
    {
        // A trailing inline comment on a scalar is valid YAML and must not become part of the value.
        // The old line-oriented parser treated `prod # active` as the workspace name and failed to
        // resolve the context.
        var config =
            "workspaces:\n" +
            "  default: prod # active\n" +
            "  items:\n" +
            "    prod:\n" +
            "      connection:\n" +
            "        context: prod-cluster # cluster\n";

        Assert.Equal("prod-cluster", SealedSecretApplyStep.ParseActiveWorkspaceContext(config));
    }

    [Fact]
    public void ParseActiveWorkspaceContext_QuotedMappingKeys_AreHonored()
    {
        // Quoted mapping keys are valid YAML; the quotes are not part of the key name.
        var config =
            "\"workspaces\":\n" +
            "  'default': prod\n" +
            "  \"items\":\n" +
            "    'prod':\n" +
            "      \"connection\":\n" +
            "        'context': prod-cluster\n";

        Assert.Equal("prod-cluster", SealedSecretApplyStep.ParseActiveWorkspaceContext(config));
    }

    [Fact]
    public void ParseActiveWorkspaceContext_FlowStyleMapping_IsHonored()
    {
        // Flow-style mappings ({ ... }) are valid YAML that the line-oriented parser could not read.
        var config =
            "workspaces:\n" +
            "  default: prod\n" +
            "  items:\n" +
            "    prod: { connection: { kind: kubernetes, context: prod-cluster } }\n";

        Assert.Equal("prod-cluster", SealedSecretApplyStep.ParseActiveWorkspaceContext(config));
    }

    [Fact]
    public void ParseActiveWorkspaceContext_MalformedYaml_ReturnsNull()
    {
        // A malformed document must fail closed (null) so the caller requires an explicit override
        // rather than applying to an arbitrary cluster.
        var config =
            "workspaces:\n" +
            "  default: prod\n" +
            "   items: broken-indent:\n" +
            "  - not: a mapping\n";

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
    public async Task WaitForSealedSecretSynced_TransientStatusProbeFailure_RetriesUntilSynced()
    {
        var statusProbeCalls = 0;

        await SealedSecretApplyStep.WaitForSealedSecretSyncedAsync(
            "db-creds", "app", "db-creds", appliedGeneration: 4,
            deadline: DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5),
            timeout: TimeSpan.FromSeconds(5),
            interval: TimeSpan.FromMilliseconds(1),
            getStatus: ct => SealedSecretApplyStep.GetSealedSecretStatusAsync(
                "app",
                "db-creds",
                "kind-radius",
                ct,
                (args, _) =>
                {
                    Assert.Equal(new[] { "get", "sealedsecret", "db-creds", "-n", "app", "-o", "json", "--context", "kind-radius" }, args);

                    statusProbeCalls++;
                    return statusProbeCalls == 1
                        ? Task.FromResult((ExitCode: 1, StdOut: "", StdErr: "Unable to connect to the server: dial tcp 127.0.0.1:6443: connect: connection refused"))
                        : Task.FromResult((ExitCode: 0, StdOut: """
                            {
                              "metadata": {
                                "generation": 4
                              },
                              "status": {
                                "observedGeneration": 4,
                                "conditions": [
                                  {
                                    "type": "Synced",
                                    "status": "True"
                                  }
                                ]
                              }
                            }
                            """, StdErr: ""));
                }),
            secretExists: _ => Task.FromResult(true),
            cancellationToken: default);

        Assert.Equal(2, statusProbeCalls);
    }

    [Fact]
    public async Task GetSealedSecretStatus_PermanentFailure_ThrowsImmediately()
    {
        // A permanent failure (here RBAC-denied) will never resolve by waiting, so the status probe
        // must surface it right away instead of masquerading as an empty status and burning the whole
        // materialization budget before reporting a misleading sync-timeout.
        var calls = 0;
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            SealedSecretApplyStep.GetSealedSecretStatusAsync(
                "app",
                "db-creds",
                "kind-radius",
                default,
                (_, _) =>
                {
                    calls++;
                    return Task.FromResult((ExitCode: 1, StdOut: "", StdErr: "Error from server (Forbidden): sealedsecrets.bitnami.com \"db-creds\" is forbidden: User cannot get resource"));
                }));

        Assert.Equal(1, calls);
        Assert.Contains("db-creds", ex.Message);
    }

    [Fact]
    public async Task GetSealedSecretStatus_NotFound_ReturnsEmptyStatusForRetry()
    {
        // A NotFound for the target SealedSecret means "not observed yet" — return an empty snapshot
        // so the poll loop keeps waiting.
        var snapshot = await SealedSecretApplyStep.GetSealedSecretStatusAsync(
            "app",
            "db-creds",
            "kind-radius",
            default,
            (_, _) => Task.FromResult((ExitCode: 1, StdOut: "", StdErr: "Error from server (NotFound): sealedsecrets.bitnami.com \"db-creds\" not found")));

        Assert.Null(snapshot.Generation);
        Assert.Null(snapshot.ObservedGeneration);
        Assert.Empty(snapshot.Conditions);
    }

    [Theory]
    [InlineData("Unable to connect to the server: dial tcp 127.0.0.1:6443: connect: connection refused")]
    [InlineData("Unable to connect to the server: net/http: TLS handshake timeout")]
    [InlineData("Error from server: etcdserver: request timed out")]
    public async Task GetSealedSecretStatus_TransientFailure_ReturnsEmptyStatusForRetry(string stderr)
    {
        var snapshot = await SealedSecretApplyStep.GetSealedSecretStatusAsync(
            "app",
            "db-creds",
            "kind-radius",
            default,
            (_, _) => Task.FromResult((ExitCode: 1, StdOut: "", StdErr: stderr)));

        Assert.Null(snapshot.Generation);
        Assert.Empty(snapshot.Conditions);
    }

    [Theory]
    [InlineData("Error from server (NotFound): sealedsecrets.bitnami.com \"db-creds\" not found", "db-creds", true)]
    [InlineData("Error from server (NotFound): sealedsecrets.bitnami.com \"other\" not found", "db-creds", false)]
    [InlineData("Error from server (NotFound): namespaces \"db-creds\" not found", "db-creds", false)]
    [InlineData("error: exec plugin: invalid apiVersion; kubelogin not found", "db-creds", false)]
    public void IsSealedSecretNotFound_MatchesOnlyTargetSealedSecret(string stderr, string name, bool expected)
    {
        Assert.Equal(expected, SealedSecretApplyStep.IsSealedSecretNotFound(stderr, name));
    }

    [Theory]
    [InlineData("Unable to connect to the server: dial tcp 127.0.0.1:6443: connect: connection refused", true)]
    [InlineData("net/http: TLS handshake timeout", true)]
    [InlineData("etcdserver: request timed out", true)]
    [InlineData("Error from server (Forbidden): sealedsecrets.bitnami.com \"db-creds\" is forbidden", false)]
    [InlineData("error: You must be logged in to the server (Unauthorized)", false)]
    public void IsTransientKubectlFailure_MatchesOnlyConnectivityFailures(string stderr, bool expected)
    {
        Assert.Equal(expected, SealedSecretApplyStep.IsTransientKubectlFailure(stderr));
    }

    [Fact]
    public async Task WaitForSealedSecretSynced_ReturnsOnceObservedGenerationMatchesSyncedTrueAndSecretExists()
    {
        var statusCalls = 0;
        var secretCalls = 0;
        await SealedSecretApplyStep.WaitForSealedSecretSyncedAsync(
            "db-creds", "app", "db-creds", appliedGeneration: 4,
            deadline: DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5),
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
                deadline: DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5),
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
                deadline: DateTimeOffset.UtcNow + TimeSpan.FromMilliseconds(10),
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
    public async Task WaitForSealedSecretSynced_ConcurrentReapplyAdvancesGeneration_SyncsAgainstLiveGeneration()
    {
        // A sibling deploy that shares an application-scoped sealed store re-applies the same manifest
        // and bumps generation to 5. That benign idempotent re-apply must not fail this wait: once the
        // controller reports Synced=True for the live generation (5) and the Secret exists, the wait
        // completes instead of throwing a "concurrent modification" failure.
        await SealedSecretApplyStep.WaitForSealedSecretSyncedAsync(
            "db-creds", "app", "db-creds", appliedGeneration: 4,
            deadline: DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5),
            timeout: TimeSpan.FromSeconds(5),
            interval: TimeSpan.FromMilliseconds(1),
            getStatus: _ => Task.FromResult(new SealedSecretApplyStep.SealedSecretStatusSnapshot(
                5,
                5,
                [new SealedSecretApplyStep.SealedSecretCondition("Synced", "True", null)])),
            secretExists: _ => Task.FromResult(true),
            cancellationToken: default);
    }

    [Fact]
    public async Task WaitForSealedSecretSynced_StatusMatchesButSecretAbsent_KeepsWaitingThenTimesOut()
    {
        var secretCalls = 0;
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            SealedSecretApplyStep.WaitForSealedSecretSyncedAsync(
                "db-creds", "app", "db-creds", appliedGeneration: 4,
                // Use a comfortable timeout (not a tiny wall-clock budget) and drive the timeout
                // deterministically from the probe. A previous 10ms budget was flaky under CI load:
                // a slow cold-JIT first iteration could consume the whole budget before a second
                // poll, so 'secretCalls > 1' occasionally saw only one call. Here the first probe
                // reports the Secret absent (so the loop keeps waiting past one iteration), then the
                // second probe hangs until the remaining-budget guard cancels it and surfaces the
                // ASPIRERADIUS058 timeout — guaranteeing at least two polls regardless of runner speed.
                deadline: DateTimeOffset.UtcNow + TimeSpan.FromMilliseconds(250),
                timeout: TimeSpan.FromMilliseconds(250),
                interval: TimeSpan.FromMilliseconds(1),
                getStatus: _ => Task.FromResult(new SealedSecretApplyStep.SealedSecretStatusSnapshot(
                    4,
                    4,
                    [new SealedSecretApplyStep.SealedSecretCondition("Synced", "True", null)])),
                secretExists: async ct =>
                {
                    secretCalls++;
                    if (secretCalls == 1)
                    {
                        return false;
                    }

                    await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
                    return false;
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
                deadline: DateTimeOffset.UtcNow + TimeSpan.FromMilliseconds(50),
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
                deadline: DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5),
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
            """, "db-creds", "app", "db-creds");

        Assert.Equal(7, generation);
    }

    [Fact]
    public void ParseGeneration_MissingGeneration_Throws_ASPIRERADIUS058()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => SealedSecretApplyStep.ParseGeneration("""
            {
              "apiVersion": "bitnami.com/v1alpha1",
              "kind": "SealedSecret",
              "metadata": {
                "name": "db-creds"
              }
            }
            """, "db-creds", "app", "db-creds"));

        Assert.Contains("ASPIRERADIUS058", ex.Message);
        Assert.Contains("app/db-creds", ex.Message);
        Assert.Contains("db-creds", ex.Message);
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
    public void ParseSecretDataKeys_ReadsDataKeyNames()
    {
        // `kubectl get secret <name> -o json` returns base64 values under `data`; only the key
        // names are extracted (values are ignored so no secret material leaves the parser).
        var keys = SealedSecretApplyStep.ParseSecretDataKeys("""
            {
              "apiVersion": "v1",
              "kind": "Secret",
              "data": {
                "username": "YWRtaW4=",
                "password": "czNjcmV0"
              }
            }
            """);

        Assert.Equal(new[] { "password", "username" }, keys.OrderBy(k => k, StringComparer.Ordinal));
    }

    [Fact]
    public void ParseSecretDataKeys_NoData_ReturnsEmpty()
    {
        var keys = SealedSecretApplyStep.ParseSecretDataKeys("""
            {
              "apiVersion": "v1",
              "kind": "Secret"
            }
            """);

        Assert.Empty(keys);
    }

    [Fact]
    public void BuildGetSecretDataArgs_TargetsNamespaceContextAndJson()
    {
        Assert.Equal(
            new[] { "get", "secret", "db-creds", "-n", "app", "-o", "json", "--context", "kind-radius" },
            SealedSecretApplyStep.BuildGetSecretDataArgs("app", "db-creds", "kind-radius"));
    }

    [Fact]
    public void FindMissingDeclaredKeys_ReturnsDeclaredKeysAbsentFromSecret()
    {
        var missing = SealedSecretApplyStep.FindMissingDeclaredKeys(
            new[] { "username", "password" },
            new HashSet<string>(StringComparer.Ordinal) { "username" });

        Assert.Equal(new[] { "password" }, missing);
    }

    [Fact]
    public void FindMissingDeclaredKeys_AllPresent_ReturnsEmpty()
    {
        var missing = SealedSecretApplyStep.FindMissingDeclaredKeys(
            new[] { "username", "password" },
            new HashSet<string>(StringComparer.Ordinal) { "username", "password", "extra" });

        Assert.Empty(missing);
    }

    [Fact]
    public void FindMissingDeclaredKeys_NoDeclaredKeys_ReturnsEmpty()
    {
        var missing = SealedSecretApplyStep.FindMissingDeclaredKeys(
            Array.Empty<string>(),
            new HashSet<string>(StringComparer.Ordinal));

        Assert.Empty(missing);
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
    public void EvaluateSealedSecretSync_AdvancedGenerationSyncedTrue_ReturnsSynced()
    {
        // A concurrent identical re-apply advanced the live generation to 5 and the controller has
        // observed and synced it; evaluating against the live generation yields Synced, not a failure.
        var decision = SealedSecretApplyStep.EvaluateSealedSecretSync(
            new SealedSecretApplyStep.SealedSecretStatusSnapshot(
                5,
                5,
                [new SealedSecretApplyStep.SealedSecretCondition("Synced", "True", null)]),
            appliedGeneration: 4);

        Assert.Equal(SealedSecretApplyStep.SealedSecretSyncDecisionKind.Synced, decision.Kind);
    }

    [Fact]
    public void EvaluateSealedSecretSync_AdvancedGenerationNotYetObserved_ReturnsWaiting()
    {
        // The live generation advanced to 5 but the controller has only observed generation 4, so the
        // wait keeps polling until the latest generation is observed rather than declaring a failure.
        var decision = SealedSecretApplyStep.EvaluateSealedSecretSync(
            new SealedSecretApplyStep.SealedSecretStatusSnapshot(
                5,
                4,
                [new SealedSecretApplyStep.SealedSecretCondition("Synced", "True", null)]),
            appliedGeneration: 4);

        Assert.Equal(SealedSecretApplyStep.SealedSecretSyncDecisionKind.Waiting, decision.Kind);
    }

    [Fact]
    public void EvaluateSealedSecretSync_AdvancedGenerationSyncedFalse_ReturnsFailed()
    {
        var decision = SealedSecretApplyStep.EvaluateSealedSecretSync(
            new SealedSecretApplyStep.SealedSecretStatusSnapshot(
                5,
                5,
                [new SealedSecretApplyStep.SealedSecretCondition("Synced", "False", "no key could decrypt secret")]),
            appliedGeneration: 4);

        Assert.Equal(SealedSecretApplyStep.SealedSecretSyncDecisionKind.Failed, decision.Kind);
    }

    [Fact]
    public async Task InvokeProbeWithRemainingBudget_HangingApply_CancelledWithin_ASPIRERADIUS066()
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            SealedSecretApplyStep.InvokeProbeWithRemainingBudgetAsync<int>(
                async ct =>
                {
                    await Task.Delay(Timeout.Infinite, ct);
                    return 0;
                },
                remaining: TimeSpan.FromMilliseconds(50),
                cancellationToken: default,
                createTimeoutException: () => SealedSecretApplyStep.CreateOperationTimeoutException(
                    "db-creds", "app", "db-creds", "apply", TimeSpan.FromMilliseconds(50))));

        stopwatch.Stop();
        Assert.Contains("ASPIRERADIUS066", ex.Message);
        Assert.Contains("apply", ex.Message);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task InvokeProbeWithRemainingBudget_CallerCancellation_SurfacesOperationCanceled()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // Caller cancellation must NOT be reported as a budget timeout (066); it surfaces as a plain
        // OperationCanceledException so the pipeline can distinguish user cancellation from a hang.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            SealedSecretApplyStep.InvokeProbeWithRemainingBudgetAsync<int>(
                async ct =>
                {
                    await Task.Delay(Timeout.Infinite, ct);
                    return 0;
                },
                remaining: TimeSpan.FromSeconds(5),
                cancellationToken: cts.Token,
                createTimeoutException: () => SealedSecretApplyStep.CreateOperationTimeoutException(
                    "db-creds", "app", "db-creds", "verify", TimeSpan.FromSeconds(5))));
    }

    [Fact]
    public void CreateOperationTimeoutException_VerifyOperation_ContainsCodeOperationAndStore()
    {
        var ex = SealedSecretApplyStep.CreateOperationTimeoutException(
            "db-creds", "app", "db-creds", "verify", TimeSpan.FromSeconds(30));

        Assert.Contains("ASPIRERADIUS066", ex.Message);
        Assert.Contains("verify", ex.Message);
        Assert.Contains("db-creds", ex.Message);
        Assert.Contains("app/db-creds", ex.Message);
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
