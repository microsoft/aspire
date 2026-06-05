// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using Aspire.Dogfooder.Commands;
using Aspire.Dogfooder.Services;
using Aspire.Dogfooder.State;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Aspire.Dogfooder;

/// <summary>
/// Dogfooder entry point. Wires up <see cref="HostApplicationBuilder"/> DI,
/// then dispatches via <see cref="RootCommand"/>. The default action runs the
/// interactive TUI; <c>self-test</c> is a Phase 2 stub.
/// </summary>
internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);

        // State graph — one ChangeNotifier shared by everything. AppState owns
        // it so screens can hand it back to Hex1bApp.Invalidate without taking
        // a direct dependency on the notifier instance.
        builder.Services.AddSingleton<ChangeNotifier>();
        builder.Services.AddSingleton<EnvironmentValidationState>();
        builder.Services.AddSingleton<AppState>();

        // Services.
        builder.Services.AddSingleton<IGitHubAuthProbe, GitHubAuthProbe>();
        builder.Services.AddSingleton<ILocalAspireCliLocator, LocalAspireCliLocator>();
        builder.Services.AddSingleton<IPackageBuildRunner, PackageBuildRunner>();
        // The vCurrent resolver is registered as both the interface and the
        // concrete VCurrentVersionResolver so the same instance can be
        // disposed (it owns an HttpClient + SemaphoreSlim) when the host
        // shuts down; the registry consumes the interface.
        builder.Services.AddSingleton<VCurrentVersionResolver>();
        builder.Services.AddSingleton<IVCurrentVersionResolver>(sp => sp.GetRequiredService<VCurrentVersionResolver>());
        builder.Services.AddSingleton<IDogfoodSessionPreparer, DogfoodSessionPreparer>();
        builder.Services.AddSingleton<Scenarios.DogfoodScenarioRegistry>();

        // Commands themselves are DI'd so they can pull resolved services
        // without manual root-container plumbing.
        builder.Services.AddSingleton<RunCommand>();
        builder.Services.AddSingleton<SelfTestCommand>();

        using var host = builder.Build();
        await host.StartAsync().ConfigureAwait(false);

        // Kick the vCurrent probe early so by the time the user clicks
        // Continue on a "Reproduce vCurrent" scenario the resolver's
        // LatestStableOrNull snapshot is populated and the build is stamped
        // with the actual released version rather than the in-development
        // fallback from eng/Versions.props. Fire-and-forget is fine: the
        // resolver caches the result and the scenario falls back when the
        // probe hasn't completed yet (or failed offline). After the version
        // resolves, chain the GitHub commit-SHA probe so the same scenario
        // can stamp ASPIRE_CLI_COMMIT alongside ASPIRE_CLI_VERSION.
        var resolver = host.Services.GetRequiredService<IVCurrentVersionResolver>();
        var stopping = host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping;
        _ = Task.Run(async () =>
        {
            var v = await resolver.GetLatestStableAsync(stopping).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(v))
            {
                _ = await resolver.GetCommitShaAsync(v, stopping).ConfigureAwait(false);
            }
        }, stopping);

        try
        {
            var root = BuildRootCommand(host.Services, host.Services.GetRequiredService<IHostApplicationLifetime>());
            return await root.Parse(args).InvokeAsync().ConfigureAwait(false);
        }
        finally
        {
            await host.StopAsync().ConfigureAwait(false);
        }
    }

    private static RootCommand BuildRootCommand(IServiceProvider services, IHostApplicationLifetime lifetime)
    {
        var root = new RootCommand("Aspire Dogfooder — Hex1b test rig for the CLI identity sidecar.")
        {
            Description = "Configures and launches dogfooding sessions with ASPIRE_CLI_* identity overrides applied.",
        };

        // The default action (no subcommand) boots the interactive TUI. We
        // wire the host's stopping token so Ctrl+C unwinds the Hex1b loop
        // and the post-shutdown SaveAsync path runs.
        root.SetAction(async (_, ct) =>
        {
            var cmd = services.GetRequiredService<RunCommand>();
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, lifetime.ApplicationStopping);
            return await cmd.RunAsync(linked.Token).ConfigureAwait(false);
        });

        var selfTest = new Command("self-test", "Automation-driven smoke test (Phase 2 stub).");
        selfTest.SetAction(async (_, ct) =>
        {
            var cmd = services.GetRequiredService<SelfTestCommand>();
            return await cmd.RunAsync(ct).ConfigureAwait(false);
        });
        root.Subcommands.Add(selfTest);

        return root;
    }
}
