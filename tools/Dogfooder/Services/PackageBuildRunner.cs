// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Aspire.Dogfooder.Services;

internal sealed class PackageBuildRunner : IPackageBuildRunner
{
    public PackageBuildRunner(ILocalAspireCliLocator cliLocator)
    {
        _cliLocator = cliLocator;
    }

    private readonly ILocalAspireCliLocator _cliLocator;

    public async Task<PackageBuildResult> RunAsync(
        PackageBuildRequest request,
        Action<string> onLine,
        CancellationToken cancellationToken)
    {
        var repoRoot = FindRepoRoot()
            ?? throw new InvalidOperationException("Could not locate repo root (no ancestor directory contains global.json).");

        // Arcade emits packages under artifacts/packages/{Configuration}/{Shipping|NonShipping}.
        // Dogfood builds are always Debug + Shipping today; if we ever want
        // Release dogfood packages we'd extend the request shape.
        var packagesDir = Path.Combine(repoRoot, "artifacts", "packages", "Debug", "Shipping");

        var (fileName, args) = BuildCommandLine(repoRoot, request);

        // Mirror every line written to onLine into an on-disk log file so the
        // user has something to grep after the TUI exits. Placed under the
        // repo's artifacts/log/dogfooder/ directory because that is git-
        // ignored and lives alongside the rest of the build's artifacts; one
        // file per invocation so concurrent dogfooder runs in different
        // worktrees don't stomp each other.
        var logDir = Path.Combine(repoRoot, "artifacts", "log", "dogfooder");
        Directory.CreateDirectory(logDir);
        var logPath = Path.Combine(
            logDir,
            $"build-{DateTime.Now:yyyyMMdd-HHmmss}-{Environment.ProcessId}.log");
        // Use a StreamWriter with AutoFlush so a crash mid-build still leaves
        // a partial-but-readable log on disk. The file is closed in the
        // finally block below.
        var logWriter = new StreamWriter(logPath, append: false) { AutoFlush = true };

        void Tee(string line)
        {
            onLine(line);
            try
            {
                logWriter.WriteLine(line);
            }
            catch
            {
                // Disk full / permissions issue — don't crash the build run
                // over a missing log file; the in-TUI log is still live.
            }
        }

        // Surface the log path as the very first line so the user sees it in
        // the Build log tab and knows where to look after the TUI exits.
        Tee($"# Log file: {logPath}");
        Tee($"$ {fileName} {string.Join(' ', args)}");

        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = repoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        // Scrub ASPIRE_CLI_* identity overrides from the child build's
        // environment. The build must run as if it were a normal repo build
        // — never as a "dogfooded" process. If the user launched the
        // Dogfooder from a shell that already had these set (e.g. from a
        // prior dogfood session), inheriting them could:
        //   * point a NuGet restore at our half-started local proxy (the
        //     proxy is only started AFTER the build completes — but the env
        //     could still be set from a stale shell),
        //   * cause Arcade's version-stamping helpers to disagree with the
        //     suffix we pass on the command line.
        // We always clear the full identity surface (Channel/Version/Commit/
        // NuGetServiceIndex) so the build sees a pristine environment.
        foreach (var name in Aspire.Shared.AspireCliIdentityEnvVars.IdentityEnvVarNames)
        {
            psi.Environment.Remove(name);
        }

        // Some build steps look at HOME / USERPROFILE; we deliberately do not
        // override the rest of the process environment so the script uses
        // whatever toolchain the user has authenticated for (e.g. dnceng
        // feed credentials).

        var sw = Stopwatch.StartNew();
        try
        {
            using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };

            proc.OutputDataReceived += (_, e) =>
            {
                if (e.Data is not null)
                {
                    Tee(e.Data);
                }
            };
            proc.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is not null)
                {
                    Tee(e.Data);
                }
            };

            proc.Start();
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            try
            {
                await proc.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Best-effort cooperative shutdown: kill the entire process tree
                // because the Arcade build spawns MSBuild + dotnet + cl/clang in a
                // deep tree and a plain Kill() would leave them running.
                try { proc.Kill(entireProcessTree: true); } catch { }
                throw;
            }

            sw.Stop();

            var nupkgs = Directory.Exists(packagesDir)
                ? Directory.EnumerateFiles(packagesDir, "*.nupkg", SearchOption.TopDirectoryOnly).ToList()
                : (IReadOnlyList<string>)Array.Empty<string>();

            Tee($"# Exit {proc.ExitCode} in {sw.Elapsed.TotalSeconds:F1}s. Log: {logPath}");

            return new PackageBuildResult(
                Success: proc.ExitCode == 0,
                PackagesDirectory: packagesDir,
                ProducedNupkgPaths: nupkgs,
                ExitCode: proc.ExitCode,
                Elapsed: sw.Elapsed);
        }
        finally
        {
            try
            {
                logWriter.Dispose();
            }
            catch
            {
                // Already best-effort throughout the runner; nothing useful to
                // do if closing the writer itself throws.
            }
        }
    }

    private static (string FileName, IReadOnlyList<string> Args) BuildCommandLine(string repoRoot, PackageBuildRequest request)
    {
        // /p:VersionSuffix overrides Arcade's default suffix-stamping pipeline:
        // see https://github.com/dotnet/arcade/blob/main/Documentation/CorePackages/Versioning.md
        // For dogfood we want a stable, predictable string in the produced
        // .nupkg filenames so the embedded NuGet feed's overlay map is
        // deterministic.
        var props = new List<string>
        {
            $"/p:VersionSuffix={request.VersionSuffix}",
            // NU5104 ("stable release should not have a prerelease
            // dependency") and NU5125 (missing readme) are pack-time
            // warnings that get promoted to errors by the repo-wide
            // TreatWarningsAsErrors=true. Both are publish-gate concerns —
            // dogfood .nupkgs never leave the in-process
            // DogfoodingNuGetServer overlay — so demote them here.
            //
            // We deliberately use WarningsNotAsErrors instead of NoWarn:
            // /p:NoWarn=... becomes an MSBuild global property which
            // *replaces* every project's '<NoWarn>$(NoWarn),1573,1591,...
            // </NoWarn>' (global properties can't be overridden in a csproj
            // PropertyGroup without TreatAsLocalProperty), wiping the
            // per-project CS1591/CS1573 suppressions and producing tens of
            // thousands of unrelated XML-doc and nullable errors across
            // tests/ and analyzers/. WarningsNotAsErrors only demotes the
            // listed codes from error back to warning and leaves NoWarn
            // untouched.
            //
            // ';' inside a /p: value must be escaped as %3B; MSBuild's
            // command-line parser otherwise splits on raw semicolons and
            // reports MSB1006 'Property is not valid' against the second
            // half.
            "/p:WarningsNotAsErrors=NU5104%3BNU5125",
        };
        if (!request.IncludeNativeBuild)
        {
            // SkipNativeBuild bypasses the AOT publish for Aspire.Cli, which
            // shaves several minutes off the build. The non-native build still
            // produces NuGet packages for the hosting/components, which is all
            // we need for proxy overlay scenarios.
            props.Add("/p:SkipNativeBuild=true");
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // build.cmd uses single-dash flags (-pack, not --pack) on the
            // outer wrapper, then forwards the rest verbatim.
            var args = new List<string> { "/c", Path.Combine(repoRoot, "build.cmd"), "-pack" };
            args.AddRange(props);
            return ("cmd.exe", args);
        }
        else
        {
            var args = new List<string> { Path.Combine(repoRoot, "build.sh"), "--pack" };
            args.AddRange(props);
            return ("/bin/bash", args);
        }
    }

    private static string? FindRepoRoot()
    {
        // Same walk-up-for-global.json strategy as LocalAspireCliLocator;
        // duplicated here rather than going through ILocalAspireCliLocator
        // because CliDirectory may be null on a fresh checkout where the CLI
        // hasn't been built yet — and "no CLI yet" is exactly when the user
        // will want to run the build.
        var startDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
        var dir = startDir is null ? null : new DirectoryInfo(startDir);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "global.json")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        return null;
    }
}
