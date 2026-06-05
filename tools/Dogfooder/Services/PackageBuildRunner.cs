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
        var arcadePackagesDir = Path.Combine(repoRoot, "artifacts", "packages", "Debug", "Shipping");

        var (fileName, args) = BuildCommandLine(repoRoot, request);

        // Routing the build log into the session workspace when one is
        // supplied keeps every artifact from a single dogfood run together
        // in one place (the user gets a single directory they can hand to
        // a bug report). Falling back to artifacts/log/dogfooder/ preserves
        // the original git-ignored location for callers that don't have a
        // workspace yet (e.g. ad-hoc command-line invocations).
        string logPath;
        if (request.BuildLogPath is { Length: > 0 } explicitPath)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(explicitPath)!);
            logPath = explicitPath;
        }
        else
        {
            var logDir = Path.Combine(repoRoot, "artifacts", "log", "dogfooder");
            Directory.CreateDirectory(logDir);
            logPath = Path.Combine(
                logDir,
                $"build-{DateTime.Now:yyyyMMdd-HHmmss}-{Environment.ProcessId}.log");
        }
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

        // Arcade's eng/build.sh always injects /p:TreatWarningsAsErrors=true
        // on the inner MSBuild command line (via eng/common/tools.sh's
        // RunBuildTool helper), which overrides anything we try to pass on
        // the outer command line — including /p:WarningsNotAsErrors=NU5104.
        // The documented escape hatch is the TreatWarningsAsErrors=false env
        // var: eng/build.sh checks for it and forwards '-warnAsError 0' down
        // into Arcade, which then flips TWAE=false on the inner MSBuild call.
        // This is the only mechanism we've found that demotes NU5104 (stable
        // depends on prerelease) back to a warning at pack time without
        // wiping per-project NoWarn (which /p:NoWarn=... would do because
        // global properties can't be overridden in a csproj <PropertyGroup>).
        // Safe for dogfooding: built .nupkgs only ever live inside the
        // in-process DogfoodingNuGetServer overlay; they are never published.
        psi.Environment["TreatWarningsAsErrors"] = "false";

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

            var arcadeNupkgs = Directory.Exists(arcadePackagesDir)
                ? Directory.EnumerateFiles(arcadePackagesDir, "*.nupkg", SearchOption.TopDirectoryOnly).ToList()
                : new List<string>();

            // When the caller (DogfoodSessionPreparer) gave us a per-session
            // packages dir, copy every produced .nupkg there so the local
            // NuGet proxy is overlaying session-owned bytes rather than
            // whatever happens to be sitting in the shared arcade output
            // dir (which could be stale from a prior run with a different
            // VersionPrefix/Suffix). The copy is small — single-digit
            // hundreds of MB at the high end — and the alternative
            // (pointing the proxy directly at arcadePackagesDir) leaks
            // state between sessions in a way that's confusing to debug.
            IReadOnlyList<string> producedNupkgs;
            string effectivePackagesDir;
            if (request.OutputPackagesDir is { Length: > 0 } outDir && arcadeNupkgs.Count > 0)
            {
                Directory.CreateDirectory(outDir);
                var copied = new List<string>(arcadeNupkgs.Count);
                foreach (var src in arcadeNupkgs)
                {
                    var dst = Path.Combine(outDir, Path.GetFileName(src));
                    File.Copy(src, dst, overwrite: true);
                    copied.Add(dst);
                }
                producedNupkgs = copied;
                effectivePackagesDir = outDir;
                Tee($"# Copied {copied.Count} .nupkg files to {outDir}");
            }
            else
            {
                producedNupkgs = arcadeNupkgs;
                effectivePackagesDir = arcadePackagesDir;
            }

            Tee($"# Exit {proc.ExitCode} in {sw.Elapsed.TotalSeconds:F1}s. Log: {logPath}");

            return new PackageBuildResult(
                Success: proc.ExitCode == 0,
                PackagesDirectory: effectivePackagesDir,
                ProducedNupkgPaths: producedNupkgs,
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
            // NB: we do NOT pass /p:NoWarn or /p:WarningsNotAsErrors here.
            //
            // /p:NoWarn=... becomes an MSBuild global property which
            // *replaces* every project's '<NoWarn>$(NoWarn),1573,1591,...
            // </NoWarn>' (global properties can't be overridden in a csproj
            // PropertyGroup without TreatAsLocalProperty), wiping the
            // per-project CS1591/CS1573 suppressions and producing tens of
            // thousands of unrelated XML-doc / nullable errors across
            // tests/ and analyzers/.
            //
            // /p:WarningsNotAsErrors=NU5104 also doesn't survive: Arcade's
            // eng/build.sh always re-injects /p:TreatWarningsAsErrors=true
            // on the inner MSBuild command line (eng/common/tools.sh's
            // RunBuildTool), so NU5104 gets re-promoted to an error and
            // the pack fails on Azure.AI.* / Milvus.Client / etc.
            //
            // The escape hatch is the TreatWarningsAsErrors=false *env var*
            // (set above in ProcessStartInfo.Environment), which eng/build.sh
            // turns into '-warnAsError 0' for the inner Arcade invocation.
        };
        if (!request.IncludeNativeBuild)
        {
            // SkipNativeBuild bypasses the AOT publish for Aspire.Cli, which
            // shaves several minutes off the build. The non-native build still
            // produces NuGet packages for the hosting/components, which is all
            // we need for proxy overlay scenarios.
            props.Add("/p:SkipNativeBuild=true");
        }

        // VersionPrefix is honoured by Arcade's versioning pipeline as the
        // major.minor.patch portion of every produced package version.
        // We use it so a local debug build of the in-development branch can
        // be stamped with whatever the actually-shipped vCurrent is
        // (eng/Versions.props on the branch carries the *next* version,
        // not the current one). When unset Arcade falls back to the value
        // from eng/Versions.props as usual.
        if (request.VersionPrefix is { Length: > 0 } prefix)
        {
            props.Add($"/p:VersionPrefix={prefix}");
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
