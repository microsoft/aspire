// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Reflection;
using Xunit;

namespace Aspire.Cli.EndToEnd.Tests.Helpers;

internal static class DotnetWatchTestHelpers
{
    internal static TemporaryWorkspace CreateWorkspace(string repoRoot, ITestOutputHelper output)
    {
        // Keeping fixtures beneath the checkout also tests SDK selection against the
        // repository's different SDK. The verifier supplies its own global.json paths.
        // A short unique leaf leaves room for the watcher's Unix socket names.
        // The pinned watcher ignores project/import edits beneath any dot-prefixed
        // ancestor, so this root and the verifier's scratch directory must be visible.
        var directory = Directory.CreateDirectory(Path.Combine(repoRoot, "wv", $"w{Guid.NewGuid().ToString("N")[..12]}"));
        var workspace = new TemporaryWorkspace(output, directory);
        TestContext.Current.KeyValueStorage["WorkspacePath"] = directory.FullName;
        if (TestContext.Current.KeyValueStorage.TryGetValue("PreserveWorkspaceOnFailure", out var preserve) && preserve is true)
        {
            workspace.Preserve();
        }

        return workspace;
    }

    internal static async Task CopyVerifierAsync(string repoRoot, TemporaryWorkspace workspace, CancellationToken cancellationToken)
    {
        await using var asset = Assembly.GetExecutingAssembly().GetManifestResourceStream("Aspire.Cli.EndToEnd.Tests.verify-watch.py")
            ?? throw new InvalidOperationException("The assembled watch verifier was not embedded.");
        using var reader = new StreamReader(asset);
        await File.WriteAllTextAsync(
            Path.Combine(workspace.Path, "verify-watch.py"),
            await reader.ReadToEndAsync(cancellationToken),
            cancellationToken);

        var configuredFeeds = Environment.GetEnvironmentVariable("WATCH_NUGET_CONFIG");
        var feeds = string.IsNullOrEmpty(configuredFeeds) ? Path.Combine(repoRoot, "NuGet.config") : configuredFeeds;
        File.Copy(feeds, Path.Combine(workspace.Path, "watch-feeds.config"));
    }
}
