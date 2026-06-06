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
            accessToken: "fake-token");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Failed downloads: native_archives_win_x64", result.Output);
        // The succeeded artifact's results should still be reported.
        Assert.Contains("native_archives_linux_x64", result.Output);
        Assert.Contains("archives=1 nupkgs=1", result.Output);
        // And the matching files should actually be on disk.
        Assert.True(File.Exists(Path.Combine(
            archivesDir,
            "native_archives_linux_x64", "Release", "Shipping", "aspire-cli-linux-x64-13.4.0.tar.gz")));
    }

    private async Task<CommandResult> RunScript(
        string collectionUri,
        string project,
        string buildId,
        string archivesTargetDir,
        string nupkgsTargetDir,
        string? accessToken,
        bool clearSystemAccessToken = false)
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
        };
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
            _artifactZips[name] = ms.ToArray();
        }

        public void AddFailingArtifact(string name)
        {
            _artifacts.Add(new MockArtifact(name));
            _failingArtifacts.Add(name);
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
