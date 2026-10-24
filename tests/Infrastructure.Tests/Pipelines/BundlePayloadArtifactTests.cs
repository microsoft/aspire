// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.Versioning;
using Aspire.TestUtilities;
using Xunit;

namespace Infrastructure.Tests;

public sealed class BundlePayloadArtifactTests(ITestOutputHelper output)
{
    [Theory]
    [UnsupportedOSPlatform("windows")]
    [RequiresTools(["pwsh", "tar"])]
    [InlineData("valid", null)]
    [InlineData("missing-contract", "apphost-server/ats-contract.json")]
    [InlineData("empty-assets", "dashboard/wwwroot/_framework/blazor.web.js")]
    [InlineData("wrong-architecture", "not ELF64 linux-x64")]
    [InlineData("non-executable", "lost executable permissions")]
    [InlineData("symbols", "contains debug symbols")]
    [InlineData("tray", "must not contain a tray payload")]
    public async Task VerifierChecksProducedArchive(string scenario, string? error)
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Linux executable modes require a Unix filesystem.");
        using var workspace = TemporaryWorkspace.Create(output);
        var source = workspace.CreateDirectory("payload").FullName;
        var layout = Path.Combine(source, "linux-x64");
        var header = new byte[64];
        // Minimal ELF64 little-endian header with e_machine=EM_X86_64.
        new byte[] { 0x7f, 0x45, 0x4c, 0x46, 2, 1 }.CopyTo(header, 0);
        header[18] = 62;
        foreach (var file in new[]
        {
            "managed/aspire-managed", "apphost-server/aspire-apphost-server",
            "dashboard/Aspire.Dashboard", "terminalhost/Aspire.TerminalHost"
        })
        {
            var path = Path.Combine(layout, file);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, header);
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        foreach (var file in new[]
        {
            "dcp/dcp", "apphost-server/ats-contract.json",
            "dashboard/wwwroot/_framework/blazor.web.js", "dashboard/libe_sqlite3.so",
            "terminalhost/libhex1binterop.so"
        })
        {
            var path = Path.Combine(layout, file);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "payload");
        }
        switch (scenario)
        {
            case "missing-contract":
                File.Delete(Path.Combine(layout, "apphost-server/ats-contract.json"));
                break;
            case "empty-assets":
                File.WriteAllText(Path.Combine(layout, "dashboard/wwwroot/_framework/blazor.web.js"), "");
                break;
            case "wrong-architecture":
                header[18] = 183;
                File.WriteAllBytes(Path.Combine(layout, "dashboard/Aspire.Dashboard"), header);
                break;
            case "non-executable":
                File.SetUnixFileMode(Path.Combine(layout, "terminalhost/Aspire.TerminalHost"), UnixFileMode.UserRead);
                break;
            case "symbols":
                File.WriteAllText(Path.Combine(layout, "dashboard/Aspire.Dashboard.dbg"), "symbols");
                break;
            case "tray":
                Directory.CreateDirectory(Path.Combine(layout, "tray"));
                break;
        }
        var archive = Path.Combine(workspace.Path, "bundle.tar.gz");
        await using (var stream = File.Create(archive))
        await using (var gzip = new GZipStream(stream, CompressionLevel.Fastest))
        {
            TarFile.CreateFromDirectory(source, gzip, includeBaseDirectory: false);
        }
        using var command = new PowerShellCommand(Path.Combine(RepoRoot.Path, "eng/scripts/verify-bundle-payload.ps1"), output)
            .WithTimeout(TimeSpan.FromMinutes(1));

        var result = await command.ExecuteAsync("-Archive", $"\"{archive}\"", "-Rid", "linux-x64");

        result.EnsureExitCode(error is null ? 0 : 1);
        Assert.Contains(error ?? "Verified linux-x64 bundle payload", result.Output);
    }
}
