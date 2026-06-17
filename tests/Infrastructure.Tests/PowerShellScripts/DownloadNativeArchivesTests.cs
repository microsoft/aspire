// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using Aspire.TestUtilities;
using Xunit;

namespace Infrastructure.Tests;

public sealed class DownloadNativeArchivesTests : IDisposable
{
    private readonly TestTempDirectory _tempDir = new();
    private readonly string _scriptPath;
    private readonly ITestOutputHelper _output;

    public DownloadNativeArchivesTests(ITestOutputHelper output)
    {
        _output = output;
        _scriptPath = Path.Combine(RepoRoot.Path, "eng", "scripts", "download-native-archives.ps1");
    }

    public void Dispose() => _tempDir.Dispose();

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task FailsWhenAccessTokenIsMissing()
    {
        var (archivesDir, nupkgsDir) = CreateTargetDirs();

        var result = await RunScript(
            collectionUri: "http://127.0.0.1:0/",
            project: "test",
            buildId: "1",
            archivesTargetDir: archivesDir,
            nupkgsTargetDir: nupkgsDir,
            accessToken: null,
            clearSystemAccessToken: true);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("AccessToken parameter not set", result.Output);
        Assert.Contains("SYSTEM_ACCESSTOKEN", result.Output);
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task FailsWhenNoArtifactsMatchPattern_ReportsAvailableArtifacts()
    {
        var (archivesDir, nupkgsDir) = CreateTargetDirs();

        using var server = new MockAzdoServer();
        server.SetArtifacts(
            new MockArtifact("PackageArtifacts", Type: "Container"),
            new MockArtifact("BlobArtifacts", Type: "Container"));
        server.Start();

        var result = await RunScript(
            collectionUri: server.CollectionUri,
            project: "test",
            buildId: "99",
            archivesTargetDir: archivesDir,
            nupkgsTargetDir: nupkgsDir,
            accessToken: "fake-token");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("No build artifacts on build 99 matched 'native_archives_*'", result.Output);
        Assert.Contains("PackageArtifacts", result.Output);
        Assert.Contains("BlobArtifacts", result.Output);
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task FailsWhenArtifactTypeIsNotContainer()
    {
        var (archivesDir, nupkgsDir) = CreateTargetDirs();

        using var server = new MockAzdoServer();
        server.SetArtifacts(
            new MockArtifact("native_archives_linux_x64", Type: "PipelineArtifact"));
        server.Start();

        var result = await RunScript(
            collectionUri: server.CollectionUri,
            project: "test",
            buildId: "1",
            archivesTargetDir: archivesDir,
            nupkgsTargetDir: nupkgsDir,
            accessToken: "fake-token");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Expected all 'native_archives_*' artifacts to be 'Container' type", result.Output);
        Assert.Contains("native_archives_linux_x64=PipelineArtifact", result.Output);
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task DownloadsAndExtractsMatchingEntries_PreservingArtifactPathComponent()
    {
        var (archivesDir, nupkgsDir) = CreateTargetDirs();

        using var server = new MockAzdoServer();
        // Two RIDs; each artifact's zip contains one archive, one nupkg, one
        // npm tgz, and some non-matching files that should be silently skipped.
        server.AddContainerArtifact("native_archives_linux_x64", new[]
        {
            ("Release/Shipping/aspire-cli-linux-x64-13.4.0.tar.gz", "tar-content"),
            ("Release/Shipping/Aspire.Cli.linux-x64.13.4.0.nupkg", "nupkg-content"),
            ("Release/Shipping/microsoft-aspire-cli-linux-x64-13.4.0.tgz", "tgz-content"),
            ("Release/Shipping/aspire-cli-linux-x64-13.4.0.tar.gz.sha512", "ignored"),
            ("Release/Shipping/SomeRandom.txt", "ignored"),
        });
        server.AddContainerArtifact("native_archives_win_x64", new[]
        {
            ("Release/Shipping/aspire-cli-win-x64-13.4.0.zip", "zip-content"),
            ("Release/Shipping/Aspire.Cli.win-x64.13.4.0.nupkg", "nupkg-content"),
            ("Release/Shipping/microsoft-aspire-cli-win-x64-13.4.0.tgz", "tgz-content"),
        });
        server.Start();

        var result = await RunScript(
            collectionUri: server.CollectionUri,
            project: "internal",
            buildId: "12345",
            archivesTargetDir: archivesDir,
            nupkgsTargetDir: nupkgsDir,
            accessToken: "fake-token");

        result.EnsureSuccessful();

        // Per-job summary lines should be present. `nupkgs` counts both
        // Aspire.Cli*.nupkg and microsoft-aspire-cli*.tgz (both land in
        // NupkgsTargetDir for stage-native-cli-tool-packages.ps1 to consume).
        Assert.Contains("native_archives_linux_x64", result.Output);
        Assert.Contains("native_archives_win_x64", result.Output);
        Assert.Contains("archives=1 nupkgs=2", result.Output);

        var extractedArchives = Directory.GetFiles(archivesDir, "*", SearchOption.AllDirectories)
            .Select(p => Path.GetRelativePath(archivesDir, p).Replace(Path.DirectorySeparatorChar, '/'))
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(
            new[]
            {
                "native_archives_linux_x64/Release/Shipping/aspire-cli-linux-x64-13.4.0.tar.gz",
                "native_archives_win_x64/Release/Shipping/aspire-cli-win-x64-13.4.0.zip",
            },
            extractedArchives);

        var extractedNupkgs = Directory.GetFiles(nupkgsDir, "*", SearchOption.AllDirectories)
            .Select(p => Path.GetRelativePath(nupkgsDir, p).Replace(Path.DirectorySeparatorChar, '/'))
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(
            new[]
            {
                "native_archives_linux_x64/Release/Shipping/Aspire.Cli.linux-x64.13.4.0.nupkg",
                "native_archives_linux_x64/Release/Shipping/microsoft-aspire-cli-linux-x64-13.4.0.tgz",
                "native_archives_win_x64/Release/Shipping/Aspire.Cli.win-x64.13.4.0.nupkg",
                "native_archives_win_x64/Release/Shipping/microsoft-aspire-cli-win-x64-13.4.0.tgz",
            },
            extractedNupkgs);
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task RefusesToExtractZipEntryThatEscapesStagingRoot()
    {
        var (archivesDir, nupkgsDir) = CreateTargetDirs();

        using var server = new MockAzdoServer();
        // Zip entry whose FullName uses `..` segments to escape the
        // <archivesDir>/<artifactName>/ staging root. The filename portion
        // (Name) still matches `aspire-cli-*.tar.gz` so the entry passes the
        // shape filter; without a zip-slip guard, Join-Path + ExtractToFile
        // would write outside the staging directory. See Zip-Slip /
        // CVE-2018-1002200 family.
        server.AddContainerArtifact("native_archives_linux_x64", new[]
        {
            ("../../../escaped/aspire-cli-linux-x64-13.4.0.tar.gz", "evil-content"),
        });
        server.Start();

        var result = await RunScript(
            collectionUri: server.CollectionUri,
            project: "internal",
            buildId: "12345",
            archivesTargetDir: archivesDir,
            nupkgsTargetDir: nupkgsDir,
            accessToken: "fake-token");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("zip-slip", result.Output);
        Assert.False(
            File.Exists(Path.Combine(_tempDir.Path, "escaped", "aspire-cli-linux-x64-13.4.0.tar.gz")),
            "Escaped file should not have been written.");
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task FailsWhenOneArtifactDownloadFails_ButReportsAllResults()
    {
        var (archivesDir, nupkgsDir) = CreateTargetDirs();

        using var server = new MockAzdoServer();
        server.AddContainerArtifact("native_archives_linux_x64", new[]
        {
            ("Release/Shipping/aspire-cli-linux-x64-13.4.0.tar.gz", "tar-content"),
            ("Release/Shipping/Aspire.Cli.linux-x64.13.4.0.nupkg", "nupkg-content"),
        });
        // Returned in the artifact list but the download URL is set to a path
        // the mock server responds to with 500 — simulates a flaky artifact
        // store while the other download still succeeds.
        server.AddFailingArtifact("native_archives_win_x64");
        server.Start();

        var result = await RunScript(
            collectionUri: server.CollectionUri,
            project: "internal",
            buildId: "12345",
            archivesTargetDir: archivesDir,
            nupkgsTargetDir: nupkgsDir,
            accessToken: "fake-token",
            maxDownloadAttempts: 2);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Failed downloads: native_archives_win_x64", result.Output);
        // The download was retried up to the attempt cap before giving up.
        Assert.Contains("after 2 attempt(s)", result.Output);
        // The succeeded artifact's results should still be reported.
        Assert.Contains("native_archives_linux_x64", result.Output);
        Assert.Contains("archives=1 nupkgs=1", result.Output);
        // And the matching files should actually be on disk.
        Assert.True(File.Exists(Path.Combine(
            archivesDir,
            "native_archives_linux_x64", "Release", "Shipping", "aspire-cli-linux-x64-13.4.0.tar.gz")));
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task RetriesTransientDownloadFailure_ThenSucceeds()
    {
        var (archivesDir, nupkgsDir) = CreateTargetDirs();

        using var server = new MockAzdoServer();
        // The download responds 500 for its first two attempts, then serves the
        // zip — exercises retrying HTTP failures. Without retry, the first
        // failure would fail the whole assemble stage.
        server.AddFlakyArtifact("native_archives_osx_x64", failTimes: 2, new[]
        {
            ("Release/Shipping/aspire-cli-osx-x64-13.4.0.tar.gz", "tar-content"),
            ("Release/Shipping/Aspire.Cli.osx-x64.13.4.0.nupkg", "nupkg-content"),
        });
        server.Start();

        var result = await RunScript(
            collectionUri: server.CollectionUri,
            project: "internal",
            buildId: "12345",
            archivesTargetDir: archivesDir,
            nupkgsTargetDir: nupkgsDir,
            accessToken: "fake-token",
            maxDownloadAttempts: 4);

        result.EnsureSuccessful();
        // Both early attempts were retried, then the third succeeded.
        Assert.Contains("Download attempt 1/4 failed for 'native_archives_osx_x64'", result.Output);
        Assert.Contains("Download attempt 2/4 failed for 'native_archives_osx_x64'", result.Output);
        Assert.Contains("archives=1 nupkgs=1", result.Output);
        Assert.True(File.Exists(Path.Combine(
            archivesDir,
            "native_archives_osx_x64", "Release", "Shipping", "aspire-cli-osx-x64-13.4.0.tar.gz")));
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task FailsFastForNonTransientHttpDownloadFailure()
    {
        var (archivesDir, nupkgsDir) = CreateTargetDirs();

        using var server = new MockAzdoServer();
        // The artifact list is valid, but the artifact download URL returns 404.
        // That is a configuration/auth-style failure, not a transient artifact
        // store failure, so retrying would only delay the actionable error.
        server.SetArtifacts(new MockArtifact("native_archives_missing"));
        server.Start();

        var result = await RunScript(
            collectionUri: server.CollectionUri,
            project: "internal",
            buildId: "12345",
            archivesTargetDir: archivesDir,
            nupkgsTargetDir: nupkgsDir,
            accessToken: "fake-token",
            maxDownloadAttempts: 4);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("non-retryable HTTP 404", result.Output);
        Assert.DoesNotContain("Download attempt 1/4 failed for 'native_archives_missing'", result.Output);
        Assert.DoesNotContain("after 4 attempt(s)", result.Output);
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task RetriesConnectionClosedDuringDownload_ThenSucceeds()
    {
        var (archivesDir, nupkgsDir) = CreateTargetDirs();

        using var server = new MockAzdoServer();
        // First attempt advertises a full zip response but closes the connection
        // after writing only a prefix. That deterministically exercises the
        // no-successful-response transport failure path, without sleeps or timing
        // races. Second attempt serves a valid zip.
        server.AddTruncatedThenValidArtifact("native_archives_osx_arm64", truncateTimes: 1, new[]
        {
            ("Release/Shipping/aspire-cli-osx-arm64-13.4.0.tar.gz", "tar-content"),
            ("Release/Shipping/Aspire.Cli.osx-arm64.13.4.0.nupkg", "nupkg-content"),
        });
        server.Start();

        var result = await RunScript(
            collectionUri: server.CollectionUri,
            project: "internal",
            buildId: "12345",
            archivesTargetDir: archivesDir,
            nupkgsTargetDir: nupkgsDir,
            accessToken: "fake-token",
            maxDownloadAttempts: 4);

        result.EnsureSuccessful();
        Assert.Contains("Download attempt 1/4 failed for 'native_archives_osx_arm64'", result.Output);
        Assert.Contains("archives=1 nupkgs=1", result.Output);
        Assert.True(File.Exists(Path.Combine(
            archivesDir,
            "native_archives_osx_arm64", "Release", "Shipping", "aspire-cli-osx-arm64-13.4.0.tar.gz")));
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task RetriesCorruptDownload_ThenSucceeds()
    {
        var (archivesDir, nupkgsDir) = CreateTargetDirs();

        using var server = new MockAzdoServer();
        // First attempt downloads successfully (HTTP 200) but the body is not a
        // valid zip — mirrors a truncated-yet-complete-looking file. The script
        // opens the zip inside the retry loop, so the open failure must trigger a
        // re-download rather than failing the stage. Second attempt serves a
        // valid zip.
        server.AddCorruptThenValidArtifact("native_archives_linux_arm64", corruptTimes: 1, new[]
        {
            ("Release/Shipping/aspire-cli-linux-arm64-13.4.0.tar.gz", "tar-content"),
            ("Release/Shipping/Aspire.Cli.linux-arm64.13.4.0.nupkg", "nupkg-content"),
        });
        server.Start();

        var result = await RunScript(
            collectionUri: server.CollectionUri,
            project: "internal",
            buildId: "12345",
            archivesTargetDir: archivesDir,
            nupkgsTargetDir: nupkgsDir,
            accessToken: "fake-token",
            maxDownloadAttempts: 4);

        result.EnsureSuccessful();
        // The corrupt download was retried (the warning carries the zip-open
        // failure), then the valid zip extracted.
        Assert.Contains("Download attempt 1/4 failed for 'native_archives_linux_arm64'", result.Output);
        Assert.Contains("archives=1 nupkgs=1", result.Output);
        Assert.True(File.Exists(Path.Combine(
            archivesDir,
            "native_archives_linux_arm64", "Release", "Shipping", "aspire-cli-linux-arm64-13.4.0.tar.gz")));
    }

    private async Task<CommandResult> RunScript(
        string collectionUri,
        string project,
        string buildId,
        string archivesTargetDir,
        string nupkgsTargetDir,
        string? accessToken,
        bool clearSystemAccessToken = false,
        int? maxDownloadAttempts = null,
        int retryBaseDelaySeconds = 0)
    {
        using var cmd = new PowerShellCommand(_scriptPath, _output)
            .WithTimeout(TimeSpan.FromMinutes(2));

        if (clearSystemAccessToken)
        {
            cmd.WithEnvironmentVariable("SYSTEM_ACCESSTOKEN", "");
        }

        var args = new List<string>
        {
            "-CollectionUri", $"\"{collectionUri}\"",
            "-Project", $"\"{project}\"",
            "-BuildId", $"\"{buildId}\"",
            "-ArchivesTargetDir", $"\"{archivesTargetDir}\"",
            "-NupkgsTargetDir", $"\"{nupkgsTargetDir}\"",
            // Keep tests fast: never sleep between retries. The mock server
            // returns its failures instantly, so backoff time is pure overhead.
            "-RetryBaseDelaySeconds", retryBaseDelaySeconds.ToString(),
        };
        if (maxDownloadAttempts is not null)
        {
            args.Add("-MaxDownloadAttempts");
            args.Add(maxDownloadAttempts.Value.ToString());
        }
        if (accessToken is not null)
        {
            args.Add("-AccessToken");
            args.Add($"\"{accessToken}\"");
        }

        return await cmd.ExecuteAsync([.. args]);
    }

    private (string archivesDir, string nupkgsDir) CreateTargetDirs()
    {
        var unique = Path.GetRandomFileName();
        var archives = Path.Combine(_tempDir.Path, unique, "archives");
        var nupkgs = Path.Combine(_tempDir.Path, unique, "nupkgs");
        return (archives, nupkgs);
    }

    /// <summary>
    /// A tiny in-process HTTP server that mimics the two AzDO endpoints the
    /// script hits: <c>GET .../_apis/build/builds/{id}/artifacts</c> (returns
    /// JSON with a <c>value</c> array of artifact descriptors) and the
    /// per-artifact <c>downloadUrl</c> which serves a zip back.
    /// </summary>
    private sealed record MockArtifact(string Name, string Type = "Container");

    private sealed class MockAzdoServer : IDisposable
    {
        private readonly HttpListener _listener;
        private readonly int _port;
        private readonly Dictionary<string, byte[]> _artifactZips = new();
        private readonly HashSet<string> _failingArtifacts = new();
        private readonly Dictionary<string, int> _flakyRemaining = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _truncatedRemaining = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _corruptRemaining = new(StringComparer.Ordinal);
        private List<MockArtifact> _artifacts = new();
        private CancellationTokenSource? _cts;
        private Task? _serverTask;

        public MockAzdoServer()
        {
            _port = GetFreeTcpPort();
            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://127.0.0.1:{_port}/");
        }

        public string CollectionUri => $"http://127.0.0.1:{_port}/";

        public void SetArtifacts(params MockArtifact[] artifacts)
        {
            _artifacts = artifacts.ToList();
        }

        public void AddContainerArtifact(string name, IEnumerable<(string path, string content)> zipEntries)
        {
            _artifacts.Add(new MockArtifact(name));
            _artifactZips[name] = BuildZip(zipEntries);
        }

        private static byte[] BuildZip(IEnumerable<(string path, string content)> zipEntries)
        {
            using var ms = new MemoryStream();
            using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            {
                foreach (var (path, content) in zipEntries)
                {
                    var entry = archive.CreateEntry(path);
                    using var stream = entry.Open();
                    var bytes = Encoding.UTF8.GetBytes(content);
                    stream.Write(bytes, 0, bytes.Length);
                }
            }
            return ms.ToArray();
        }

        public void AddFailingArtifact(string name)
        {
            _artifacts.Add(new MockArtifact(name));
            _failingArtifacts.Add(name);
        }

        /// <summary>
        /// Registers an artifact whose download responds with HTTP 500 for its
        /// first <paramref name="failTimes"/> requests and then serves the zip,
        /// simulating a transient artifact-store failure that the script should
        /// retry through.
        /// </summary>
        public void AddFlakyArtifact(string name, int failTimes, IEnumerable<(string path, string content)> zipEntries)
        {
            _artifacts.Add(new MockArtifact(name));
            _artifactZips[name] = BuildZip(zipEntries);
            _flakyRemaining[name] = failTimes;
        }

        /// <summary>
        /// Registers an artifact whose first <paramref name="truncateTimes"/>
        /// requests return HTTP 200 with a Content-Length for the full zip but
        /// close the connection after writing only a prefix. This exercises a
        /// deterministic mid-transfer failure without relying on timing.
        /// </summary>
        public void AddTruncatedThenValidArtifact(string name, int truncateTimes, IEnumerable<(string path, string content)> zipEntries)
        {
            _artifacts.Add(new MockArtifact(name));
            _artifactZips[name] = BuildZip(zipEntries);
            _truncatedRemaining[name] = truncateTimes;
        }

        /// <summary>
        /// Registers an artifact whose download responds 200 with corrupt
        /// (non-zip) bytes for its first <paramref name="corruptTimes"/> requests
        /// and then serves the real zip. Simulates a connection dropped
        /// mid-transfer that yields a truncated-yet-complete-looking file the
        /// script only detects when opening the zip — which should still retry.
        /// </summary>
        public void AddCorruptThenValidArtifact(string name, int corruptTimes, IEnumerable<(string path, string content)> zipEntries)
        {
            _artifacts.Add(new MockArtifact(name));
            _artifactZips[name] = BuildZip(zipEntries);
            _corruptRemaining[name] = corruptTimes;
        }

        public void Start()
        {
            _listener.Start();
            _cts = new CancellationTokenSource();
            _serverTask = Task.Run(() => RunAsync(_cts.Token));
        }

        private async Task RunAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                HttpListenerContext ctx;
                try
                {
                    ctx = await _listener.GetContextAsync().WaitAsync(token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { return; }
                catch (HttpListenerException) { return; }
                catch (ObjectDisposedException) { return; }

                try
                {
                    HandleRequest(ctx);
                }
                catch
                {
                    // Best-effort: don't crash the server loop on per-request errors.
                    try { ctx.Response.StatusCode = 500; ctx.Response.Close(); } catch { }
                }
            }
        }

        private void HandleRequest(HttpListenerContext ctx)
        {
            var path = ctx.Request.Url?.AbsolutePath ?? string.Empty;

            // Artifact list endpoint.
            if (path.EndsWith("/_apis/build/builds/" + GetBuildIdFromPath(path) + "/artifacts", StringComparison.Ordinal)
                || path.Contains("/_apis/build/builds/", StringComparison.Ordinal) && path.EndsWith("/artifacts", StringComparison.Ordinal))
            {
                var artifactDescriptors = _artifacts.Select(a => new
                {
                    name = a.Name,
                    resource = new
                    {
                        type = a.Type,
                        downloadUrl = $"{CollectionUri}_artifact/{a.Name}.zip",
                    },
                }).ToArray();

                var payload = JsonSerializer.Serialize(new { value = artifactDescriptors });
                var bytes = Encoding.UTF8.GetBytes(payload);
                ctx.Response.ContentType = "application/json";
                ctx.Response.ContentLength64 = bytes.Length;
                ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
                ctx.Response.Close();
                return;
            }

            // Per-artifact download endpoint.
            if (path.StartsWith("/_artifact/", StringComparison.Ordinal))
            {
                var fileName = Path.GetFileNameWithoutExtension(path); // strip .zip
                if (_failingArtifacts.Contains(fileName))
                {
                    ctx.Response.StatusCode = 500;
                    ctx.Response.Close();
                    return;
                }
                // Flaky artifacts fail their first N requests, then fall through
                // to serving the zip. The HttpListener loop handles requests one
                // at a time, so this counter needs no extra synchronization.
                if (_flakyRemaining.TryGetValue(fileName, out var remaining) && remaining > 0)
                {
                    _flakyRemaining[fileName] = remaining - 1;
                    ctx.Response.StatusCode = 500;
                    ctx.Response.Close();
                    return;
                }
                // Truncated artifacts start like successful downloads, then close
                // early. Content-Length stays at the full zip size so the client
                // sees a deterministic mid-response transport failure instead of
                // an HTTP status retry.
                if (_truncatedRemaining.TryGetValue(fileName, out var truncateLeft) && truncateLeft > 0
                    && _artifactZips.TryGetValue(fileName, out var zipBytes))
                {
                    _truncatedRemaining[fileName] = truncateLeft - 1;
                    var prefixLength = Math.Min(16, zipBytes.Length - 1);
                    ctx.Response.ContentType = "application/zip";
                    ctx.Response.ContentLength64 = zipBytes.Length;
                    ctx.Response.OutputStream.Write(zipBytes, 0, prefixLength);
                    ctx.Response.OutputStream.Flush();
                    ctx.Response.Abort();
                    return;
                }
                // Corrupt artifacts respond 200 with non-zip bytes for their first
                // N requests, then serve the real zip — exercising the script's
                // open-the-zip integrity check inside the retry loop.
                if (_corruptRemaining.TryGetValue(fileName, out var corruptLeft) && corruptLeft > 0)
                {
                    _corruptRemaining[fileName] = corruptLeft - 1;
                    var garbage = Encoding.UTF8.GetBytes("this is not a zip file");
                    ctx.Response.ContentType = "application/zip";
                    ctx.Response.ContentLength64 = garbage.Length;
                    ctx.Response.OutputStream.Write(garbage, 0, garbage.Length);
                    ctx.Response.Close();
                    return;
                }
                if (_artifactZips.TryGetValue(fileName, out var bytes))
                {
                    ctx.Response.ContentType = "application/zip";
                    ctx.Response.ContentLength64 = bytes.Length;
                    ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
                    ctx.Response.Close();
                    return;
                }
                ctx.Response.StatusCode = 404;
                ctx.Response.Close();
                return;
            }

            ctx.Response.StatusCode = 404;
            ctx.Response.Close();
        }

        private static string GetBuildIdFromPath(string path)
        {
            // ".../_apis/build/builds/{id}/artifacts" — pull {id}
            var marker = "/builds/";
            var i = path.IndexOf(marker, StringComparison.Ordinal);
            if (i < 0) { return ""; }
            var rest = path.Substring(i + marker.Length);
            var slash = rest.IndexOf('/');
            return slash < 0 ? rest : rest[..slash];
        }

        private static int GetFreeTcpPort()
        {
            var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            l.Start();
            var port = ((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            return port;
        }

        public void Dispose()
        {
            _cts?.Cancel();
            try { _listener.Stop(); } catch { }
            try { _listener.Close(); } catch { }
            try { _serverTask?.Wait(TimeSpan.FromSeconds(2)); } catch { }
            _cts?.Dispose();
        }
    }
}
