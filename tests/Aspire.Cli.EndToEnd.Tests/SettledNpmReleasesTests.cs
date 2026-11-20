// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aspire.Cli.EndToEnd.Tests.Helpers;
using Aspire.TestUtilities;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Aspire.Cli.EndToEnd.Tests;

/// <summary>
/// Verifies the npm release-age cutoff that <see cref="CliE2EAutomatorHelpers.PrepareDockerEnvironmentAsync"/> applies
/// to Docker E2E shells. A local registry reproduces the state the public registry is in right after a publish: the
/// packument already lists the new version, but its tarball still returns 404.
/// </summary>
public sealed class SettledNpmReleasesTests
{
    private const string PackageName = "aspire-e2e-settle-probe";

    [Fact]
    [RequiresTools(["bash", "npm"])]
    public async Task SettledNpmReleasesCommand_SkipsReleaseWhoseTarballIsNotYetAvailable()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "The cutoff command targets the Linux bash shells used by the Docker E2E tests.");

        var cancellationToken = TestContext.Current.CancellationToken;
        var now = DateTimeOffset.UtcNow;
        await using var registry = await FakeNpmRegistry.StartAsync(now, cancellationToken);
        var workspace = Directory.CreateTempSubdirectory("aspire-npm-settle-");

        try
        {
            File.WriteAllText(Path.Combine(workspace.FullName, "package.json"), """{ "name": "consumer", "version": "1.0.0", "private": true }""");
            File.WriteAllText(Path.Combine(workspace.FullName, "user.npmrc"), string.Empty);

            var install = $"npm install {PackageName}@^1.0.0 --registry={registry.Url} --userconfig=user.npmrc --cache=npm-cache --no-audit --no-fund --prefer-online";

            // Without the cutoff, npm picks the just-published 1.1.0 and fails the same way CI did.
            var unsettled = await RunBashAsync(install, workspace.FullName, cancellationToken);
            Assert.NotEqual(0, unsettled.ExitCode);
            Assert.Contains("E404", unsettled.Output);

            var settled = await RunBashAsync($"{CliE2EAutomatorHelpers.GetSettledNpmReleasesCommand(now)} && {install}", workspace.FullName, cancellationToken);
            Assert.True(settled.ExitCode == 0, $"npm install with the release-age cutoff failed:{Environment.NewLine}{settled.Output}");

            var installedManifest = JsonNode.Parse(File.ReadAllText(Path.Combine(workspace.FullName, "node_modules", PackageName, "package.json")));
            Assert.Equal("1.0.0", installedManifest?["version"]?.GetValue<string>());
        }
        finally
        {
            workspace.Delete(recursive: true);
        }
    }

    [Fact]
    public void GetSettledNpmReleasesCommand_ExportsCutoffOneSettleTimeBeforeNow()
    {
        var command = CliE2EAutomatorHelpers.GetSettledNpmReleasesCommand(new DateTimeOffset(2026, 10, 31, 12, 30, 15, TimeSpan.FromHours(-7)));

        Assert.Equal("export npm_config_before=2026-10-30T19:30:15Z", command);
    }

    private static async Task<(int ExitCode, string Output)> RunBashAsync(string command, string workingDirectory, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo("bash", ["-c", command])
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        // Keep the host's npm configuration out of the comparison.
        foreach (var key in startInfo.Environment.Keys.Where(k => k.StartsWith("npm_config_", StringComparison.OrdinalIgnoreCase)).ToList())
        {
            startInfo.Environment.Remove(key);
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start bash.");
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }

        return (process.ExitCode, await stdout + await stderr);
    }

    private sealed class FakeNpmRegistry(WebApplication application) : IAsyncDisposable
    {
        public string Url => $"{application.Urls.Single()}/";

        public static async Task<FakeNpmRegistry> StartAsync(DateTimeOffset now, CancellationToken cancellationToken)
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
            builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
            var application = builder.Build();

            var settledTarball = CreateTarball("1.0.0");
            var freshTarball = CreateTarball("1.1.0");

            application.MapGet($"/{PackageName}", (HttpContext context) =>
            {
                var registryUrl = $"{context.Request.Scheme}://{context.Request.Host}/";
                var packument = new JsonObject
                {
                    ["name"] = PackageName,
                    ["dist-tags"] = new JsonObject { ["latest"] = "1.1.0" },
                    ["versions"] = new JsonObject
                    {
                        ["1.0.0"] = CreateVersion(registryUrl, "1.0.0", settledTarball),
                        // Listed in metadata, but the tarball is not downloadable yet.
                        ["1.1.0"] = CreateVersion(registryUrl, "1.1.0", freshTarball),
                    },
                    ["time"] = new JsonObject
                    {
                        ["created"] = now.AddDays(-30).ToString("O"),
                        ["modified"] = now.AddMinutes(-2).ToString("O"),
                        ["1.0.0"] = now.AddDays(-30).ToString("O"),
                        ["1.1.0"] = now.AddMinutes(-2).ToString("O"),
                    },
                };
                return Results.Text(packument.ToJsonString(), "application/json");
            });
            application.MapGet($"/{PackageName}/-/{PackageName}-1.0.0.tgz", () => Results.Bytes(settledTarball, "application/octet-stream"));
            application.MapFallback(() => Results.Json(new { error = "Not found" }, statusCode: StatusCodes.Status404NotFound));

            try
            {
                await application.StartAsync(cancellationToken);
                return new FakeNpmRegistry(application);
            }
            catch
            {
                await application.DisposeAsync();
                throw;
            }
        }

        public ValueTask DisposeAsync() => application.DisposeAsync();

        private static JsonObject CreateVersion(string registryUrl, string version, byte[] tarball) => new()
        {
            ["name"] = PackageName,
            ["version"] = version,
            ["dist"] = new JsonObject
            {
                ["tarball"] = $"{registryUrl}{PackageName}/-/{PackageName}-{version}.tgz",
                ["shasum"] = Convert.ToHexStringLower(SHA1.HashData(tarball)),
                ["integrity"] = $"sha512-{Convert.ToBase64String(SHA512.HashData(tarball))}",
            },
        };

        private static byte[] CreateTarball(string version)
        {
            using var output = new MemoryStream();
            using (var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true))
            using (var tar = new TarWriter(gzip, TarEntryFormat.Ustar))
            {
                var manifest = JsonSerializer.SerializeToUtf8Bytes(new { name = PackageName, version });
                tar.WriteEntry(new UstarTarEntry(TarEntryType.RegularFile, "package/package.json")
                {
                    DataStream = new MemoryStream(manifest),
                    Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead,
                });
            }

            return output.ToArray();
        }
    }
}
