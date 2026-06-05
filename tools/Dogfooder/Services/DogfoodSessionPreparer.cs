// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using Aspire.Dogfooder.Scenarios;
using Aspire.Dogfooder.State;
using Aspire.Shared;

namespace Aspire.Dogfooder.Services;

/// <summary>
/// Async transform from a <see cref="DogfoodSession"/> to the set of
/// environment-variable overrides the embedded shell should apply. Resolves
/// the session's scenario, asks the scenario for its <see cref="ScenarioPlan"/>,
/// then runs the optional pre-flight steps (package build, local NuGet
/// server startup) before stamping the env-var plan onto the session.
/// </summary>
/// <remarks>
/// SessionTerminalContent consumes the resulting <see cref="SessionEnvironmentPlan"/>
/// and types the equivalent shell commands into the spawned shell via
/// <c>Hex1bTerminalAutomator</c> rather than mutating
/// <c>Hex1bTerminalProcessOptions.Environment</c> behind the user's back —
/// making the dogfooding setup auditable in the terminal scrollback.
/// </remarks>
internal interface IDogfoodSessionPreparer
{
    /// <summary>
    /// Runs the preparation pipeline:
    /// <list type="number">
    ///   <item>Resolve the chosen <c>IDogfoodScenario</c> and ask it for a <see cref="ScenarioPlan"/>.</item>
    ///   <item>Optionally invoke <c>./build.sh --pack</c> with the scenario's version suffix.</item>
    ///   <item>Optionally start a per-session <see cref="DogfoodingNuGetServer"/> overlaying the built packages.</item>
    ///   <item>Compute the <see cref="SessionEnvironmentPlan"/> the terminal will apply.</item>
    /// </list>
    /// All progress is appended to <see cref="DogfoodSession.Preparation"/> so
    /// SessionPreparationContent can render it live.
    /// </summary>
    Task<PreparationResult> PrepareAsync(DogfoodSession session, CancellationToken cancellationToken);
}

/// <param name="IdentityOverrides">
/// Ordered list of <c>(name, value)</c> pairs for the <c>ASPIRE_CLI_*</c>
/// identity overrides. Ordered (not a dictionary) so the commands appear in
/// a stable, predictable sequence when typed into the shell.
/// </param>
/// <param name="PathPrependDir">
/// Directory to prepend to <c>PATH</c> (typically the locally-built
/// <c>artifacts/bin/Aspire.Cli/...</c>), or null when no local CLI was found.
/// </param>
internal sealed record SessionEnvironmentPlan(
    IReadOnlyList<KeyValuePair<string, string>> IdentityOverrides,
    string? PathPrependDir);

/// <param name="Success">True when every requested phase completed successfully.</param>
/// <param name="Plan">The plan that was stamped onto <see cref="DogfoodSession.Plan"/>. Null on failure.</param>
/// <param name="NuGetServer">
/// The started server when the scenario's
/// <see cref="ScenarioPlan.UseLocalNuGetProxy"/> was true. Caller is
/// responsible for disposing it when the session ends.
/// </param>
internal sealed record PreparationResult(
    bool Success,
    SessionEnvironmentPlan? Plan,
    DogfoodingNuGetServer? NuGetServer);

internal sealed class DogfoodSessionPreparer : IDogfoodSessionPreparer
{
    public DogfoodSessionPreparer(
        ILocalAspireCliLocator cliLocator,
        IPackageBuildRunner buildRunner,
        DogfoodScenarioRegistry scenarioRegistry)
    {
        _cliLocator = cliLocator;
        _buildRunner = buildRunner;
        _scenarioRegistry = scenarioRegistry;
    }

    private readonly ILocalAspireCliLocator _cliLocator;
    private readonly IPackageBuildRunner _buildRunner;
    private readonly DogfoodScenarioRegistry _scenarioRegistry;

    public async Task<PreparationResult> PrepareAsync(DogfoodSession session, CancellationToken cancellationToken)
    {
        var prep = session.Preparation ??= new SessionPreparationState();
        var scenario = _scenarioRegistry.GetOrDefault(session.Config.ScenarioId);
        var scenarioPlan = scenario.Build(session.Config.Inputs);
        session.ScenarioPlan = scenarioPlan;

        // Create the per-session workspace FIRST so every downstream phase
        // can route its outputs (build log, copied packages, NuGet global-
        // packages cache, eventual dogfood.json) into one place. The
        // workspace persists across re-runs of the same session — if the
        // user closes and reopens the session window we keep using the
        // same temp dir so the inspection-after-the-fact UX is consistent.
        var workspace = session.Workspace ??= SessionWorkspace.Create();
        prep.Append($"# Workspace: {workspace.Root}");

        prep.Append($"# Scenario: {scenario.DisplayName} ({scenario.Id})");
        prep.Append($"#   Channel: {scenarioPlan.Channel}{(scenarioPlan.PrNumber is int n ? $" #{n}" : "")}");
        if (!string.IsNullOrWhiteSpace(scenarioPlan.VersionOverride))
        {
            prep.Append($"#   Version override: {scenarioPlan.VersionOverride}");
        }
        if (!string.IsNullOrWhiteSpace(scenarioPlan.PackageVersionPrefix))
        {
            prep.Append($"#   Build VersionPrefix: {scenarioPlan.PackageVersionPrefix}");
        }
        if (scenarioPlan.BuildPackagesBeforeLaunch)
        {
            prep.Append($"#   Build packages: VersionSuffix='{scenarioPlan.PackageVersionSuffix}' IncludeNativeBuild={scenarioPlan.IncludeNativeBuild}");
        }
        if (scenarioPlan.UseLocalNuGetProxy)
        {
            prep.Append($"#   Local NuGet proxy: ON (overlay {workspace.PackagesDir})");
        }

        // Phase 1: optional package build.
        if (scenarioPlan.BuildPackagesBeforeLaunch)
        {
            prep.SetPhase(SessionPreparationState.Phase.Building);
            // Empty suffix means "no -suffix appended" — produces e.g. 13.4.2
            // packages rather than 13.4.2-dogfood.x. Build runner accepts an
            // empty string for this case.
            var suffix = scenarioPlan.PackageVersionSuffix ?? "";

            try
            {
                var buildLogPath = Path.Combine(workspace.LogsDir, "build.log");
                var buildResult = await _buildRunner.RunAsync(
                    new PackageBuildRequest(
                        VersionSuffix: suffix,
                        IncludeNativeBuild: scenarioPlan.IncludeNativeBuild,
                        VersionPrefix: scenarioPlan.PackageVersionPrefix,
                        OutputPackagesDir: workspace.PackagesDir,
                        BuildLogPath: buildLogPath),
                    prep.Append,
                    cancellationToken).ConfigureAwait(false);
                prep.Append($"# Build exited {buildResult.ExitCode} in {buildResult.Elapsed.TotalSeconds:F1}s, {buildResult.ProducedNupkgPaths.Count} .nupkg files in {buildResult.PackagesDirectory}");
                if (!buildResult.Success)
                {
                    prep.SetPhase(SessionPreparationState.Phase.Failed, $"Build failed (exit {buildResult.ExitCode}).");
                    return new PreparationResult(false, null, null);
                }
            }
            catch (Exception ex)
            {
                prep.Append($"# Build threw: {ex.Message}");
                prep.SetPhase(SessionPreparationState.Phase.Failed, ex.Message);
                return new PreparationResult(false, null, null);
            }
        }

        // Phase 2: optional NuGet server.
        DogfoodingNuGetServer? server = null;
        string? serviceIndexUrl = null;
        if (scenarioPlan.UseLocalNuGetProxy)
        {
            prep.SetPhase(SessionPreparationState.Phase.StartingProxy);
            // Always overlay the session-local packages dir. If the scenario
            // requested a custom dir we honour it (e.g. the user manually
            // dropped some .nupkg files), but the default is the workspace
            // packages dir so the proxy serves the bytes we just built into
            // this session.
            var dir = scenarioPlan.LocalPackageSourceDir ?? workspace.PackagesDir;
            prep.Append($"# Starting DogfoodingNuGetServer with local overrides from {dir}");

            try
            {
                server = new DogfoodingNuGetServer()
                    .AddUpstream(new Uri("https://api.nuget.org/v3/index.json"))
                    .AddLocalOverrides(dir);
                await server.StartAsync(cancellationToken).ConfigureAwait(false);
                serviceIndexUrl = server.ServiceIndexUri!.ToString();
                prep.Append($"# Listening on {serviceIndexUrl} ({server.LocalPackageIdCount} ids, {server.LocalPackageCount} versions)");

                var traffic = session.NuGetTraffic ??= new NuGetTrafficState();
                traffic.Backfill(server.RecentEvents);
                server.TrafficObserved += traffic.Append;
            }
            catch (Exception ex)
            {
                prep.Append($"# Server startup failed: {ex.Message}");
                prep.SetPhase(SessionPreparationState.Phase.Failed, ex.Message);
                if (server is not null)
                {
                    await server.DisposeAsync().ConfigureAwait(false);
                }
                return new PreparationResult(false, null, null);
            }
        }

        // Phase 3: compute environment plan. Service-index URL from the
        // started server takes precedence over the manual scenario override —
        // we want the proxy to be used when the scenario opted in.
        var plan = BuildPlan(scenarioPlan, serviceIndexUrl, workspace);
        session.Plan = plan;

        // Phase 4: write the dogfood.json manifest. Best-effort; failure is
        // logged but doesn't fail the session because the manifest is
        // documentation, not a hard input downstream.
        var manifest = new DogfoodManifest(
            CreatedAt: DateTimeOffset.UtcNow,
            ScenarioId: scenario.Id,
            ScenarioDisplayName: scenario.DisplayName,
            Inputs: session.Config.Inputs,
            ResolvedVCurrentVersion: scenarioPlan.VersionOverride,
            PackageVersionStamp: scenarioPlan.PackageVersionPrefix
                ?? (scenarioPlan.PackageVersionSuffix is { Length: > 0 } s ? $"(suffix: {s})" : null),
            WorkspaceRoot: workspace.Root,
            LogsDir: workspace.LogsDir,
            NuGetCacheDir: workspace.NuGetCacheDir,
            NuGetHttpCacheDir: workspace.NuGetHttpCacheDir,
            PackagesDir: workspace.PackagesDir);
        workspace.WriteManifest(manifest);
        prep.Append($"# Wrote manifest: {workspace.DogfoodJsonPath}");

        prep.SetPhase(SessionPreparationState.Phase.Complete);
        return new PreparationResult(true, plan, server);
    }

    private SessionEnvironmentPlan BuildPlan(ScenarioPlan scenarioPlan, string? nugetServiceIndexFromServer, SessionWorkspace workspace)
    {
        var overrides = new List<KeyValuePair<string, string>>();

        // Channel is a closed enum on our side but a free-form string on the
        // CLI's side (the CLI accepts anything because PRs are dynamic). Emit
        // the lowercased name except for Pr which carries its number.
        var channelValue = scenarioPlan.Channel switch
        {
            ChannelKind.Stable => "stable",
            ChannelKind.Staging => "staging",
            ChannelKind.Daily => "daily",
            ChannelKind.Local => "local",
            ChannelKind.Pr when scenarioPlan.PrNumber is int n => $"pr-{n.ToString(CultureInfo.InvariantCulture)}",
            // Pr channel with no PrNumber is a UI-state bug; emit a placeholder
            // so the misconfiguration is visible in the launched terminal
            // rather than silently degrading to whatever the CLI's default is.
            ChannelKind.Pr => "pr-MISSING",
            _ => "local",
        };
        overrides.Add(new(AspireCliIdentityEnvVars.Channel, channelValue));

        if (!string.IsNullOrWhiteSpace(scenarioPlan.VersionOverride))
        {
            overrides.Add(new(AspireCliIdentityEnvVars.Version, scenarioPlan.VersionOverride));
        }

        if (!string.IsNullOrWhiteSpace(scenarioPlan.CommitOverride))
        {
            overrides.Add(new(AspireCliIdentityEnvVars.Commit, scenarioPlan.CommitOverride));
        }

        // Local NuGet server URL wins over manual override (when both are
        // set, the scenario clearly wants the live server). Manual override
        // is still useful for scenarios that want to point at a different
        // host entirely.
        var nugetIndex = !string.IsNullOrWhiteSpace(nugetServiceIndexFromServer)
            ? nugetServiceIndexFromServer
            : scenarioPlan.NuGetServiceIndexOverride;
        if (!string.IsNullOrWhiteSpace(nugetIndex))
        {
            overrides.Add(new(AspireCliIdentityEnvVars.NuGetServiceIndex, nugetIndex));
        }

        // Point NuGet's global-packages cache at the per-session workspace
        // so the CLI's restore path can never satisfy a request from bytes
        // the user already has cached machine-wide. Without this the proxy
        // looks like it's serving every request but NuGet quietly hits its
        // existing cache for any package id that was already downloaded
        // from nuget.org — which defeats the whole point of overlaying
        // session-local packages with the same id/version. Cold cache per
        // session is a few extra MB on disk but eliminates an entire class
        // of "but I built it, why isn't it being used" confusion.
        // See https://learn.microsoft.com/nuget/consume-packages/managing-the-global-packages-and-cache-folders
        overrides.Add(new("NUGET_PACKAGES", workspace.NuGetCacheDir));

        // CRITICAL companion to NUGET_PACKAGES: NuGet keeps a *separate*
        // v3 HTTP cache for service index / search / registration / flat-
        // container responses (default ~/.local/share/NuGet/v3-cache on
        // Unix, ~/AppData/Local/NuGet/v3-cache on Windows). Without
        // isolating this, `dotnet package search aspire.projecttemplates`
        // returns the cached nuget.org response from earlier non-dogfood
        // runs and NEVER hits our proxy — the symptom is an empty NuGet
        // analyzer tab combined with template versions that don't match
        // what was just built. Isolating both caches is the only way to
        // guarantee the CLI actually exercises the proxy.
        overrides.Add(new("NUGET_HTTP_CACHE_PATH", workspace.NuGetHttpCacheDir));

        return new SessionEnvironmentPlan(overrides, _cliLocator.CliDirectory);
    }
}
