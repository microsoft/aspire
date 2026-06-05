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
/// responsible for registering this with the
/// <see cref="DogfoodingNuGetServerRegistry"/> so it is disposed when the
/// session window closes.
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

        prep.Append($"# Scenario: {scenario.DisplayName} ({scenario.Id})");
        prep.Append($"#   Channel: {scenarioPlan.Channel}{(scenarioPlan.PrNumber is int n ? $" #{n}" : "")}");
        if (!string.IsNullOrWhiteSpace(scenarioPlan.VersionOverride))
        {
            prep.Append($"#   Version override: {scenarioPlan.VersionOverride}");
        }
        if (scenarioPlan.BuildPackagesBeforeLaunch)
        {
            prep.Append($"#   Build packages: VersionSuffix='{scenarioPlan.PackageVersionSuffix}' IncludeNativeBuild={scenarioPlan.IncludeNativeBuild}");
        }
        if (scenarioPlan.UseLocalNuGetProxy)
        {
            prep.Append($"#   Local NuGet proxy: ON (overlay {scenarioPlan.LocalPackageSourceDir ?? "default packages dir"})");
        }

        // Phase 1: optional package build.
        if (scenarioPlan.BuildPackagesBeforeLaunch)
        {
            prep.SetPhase(SessionPreparationState.Phase.Building);
            // Empty suffix means "no -suffix appended" — produces e.g. 13.5.0
            // packages rather than 13.5.0-dogfood.x. Build runner accepts an
            // empty string for this case.
            var suffix = scenarioPlan.PackageVersionSuffix ?? "";

            try
            {
                var buildResult = await _buildRunner.RunAsync(
                    new PackageBuildRequest(suffix, scenarioPlan.IncludeNativeBuild),
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
            var dir = scenarioPlan.LocalPackageSourceDir
                ?? Path.Combine(FindRepoRootOrCwd(), "artifacts", "packages", "Debug", "Shipping");
            prep.Append($"# Starting DogfoodingNuGetServer with local overrides from {dir}");

            try
            {
                server = new DogfoodingNuGetServer()
                    .AddUpstream(new Uri("https://api.nuget.org/v3/index.json"))
                    .AddLocalOverrides(dir);
                await server.StartAsync(cancellationToken).ConfigureAwait(false);
                serviceIndexUrl = server.ServiceIndexUri!.ToString();
                prep.Append($"# Listening on {serviceIndexUrl} ({server.LocalPackageIdCount} ids, {server.LocalPackageCount} versions)");

                // Wire the server's events into the per-session traffic
                // state so the NuGet analyzer tab updates live. The session
                // owns the state object so it survives across renders.
                var traffic = session.NuGetTraffic ??= new NuGetTrafficState();
                traffic.Backfill(server.RecentEvents);
                // Paired (request + response) event eliminates the fragile
                // id-rematch dance the prior version performed.
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
        var plan = BuildPlan(scenarioPlan, serviceIndexUrl);
        session.Plan = plan;
        prep.SetPhase(SessionPreparationState.Phase.Complete);
        return new PreparationResult(true, plan, server);
    }

    private SessionEnvironmentPlan BuildPlan(ScenarioPlan scenarioPlan, string? nugetServiceIndexFromServer)
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

        return new SessionEnvironmentPlan(overrides, _cliLocator.CliDirectory);
    }

    private static string FindRepoRootOrCwd()
    {
        // Mirrors PackageBuildRunner.FindRepoRoot's walk-up-for-global.json,
        // duplicated here because making it public on the locator would
        // widen the API surface for a single internal use.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "global.json")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        return Environment.CurrentDirectory;
    }
}
