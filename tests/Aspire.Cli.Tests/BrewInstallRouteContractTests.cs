// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Acquisition;
using Aspire.Cli.Bundles;
using Aspire.Cli.Utils;

namespace Aspire.Cli.Tests;

// Cross-consumer contract: the {"source":"brew"} install sidecar wire string
// is read by three independent code paths — the self-update gate, the bundle
// extract-dir router, and the aspire-home install-route resolver. If any one
// of them stops recognizing the brew wire string (e.g. someone renames the
// enum value, edits the constant, or refactors the sidecar reader), the brew
// route's runtime contract silently breaks. These tests construct a real
// brew-shape sidecar on disk and assert each consumer's response stays in
// lockstep, so a regression in any one consumer fails this test rather than
// being discovered in production.
public class BrewInstallRouteContractTests
{
    [Fact]
    public void BrewSidecar_IsRecognizedByAllConsumers()
    {
        using var temp = new TestTempDirectory();
        var binaryDir = Directory.CreateDirectory(Path.Combine(temp.Path, "bin"));
        var binaryPath = Path.Combine(binaryDir.FullName, OperatingSystem.IsWindows() ? "aspire.exe" : "aspire");
        File.WriteAllText(binaryPath, string.Empty);
        File.WriteAllText(
            Path.Combine(binaryDir.FullName, InstallSidecarReader.SidecarFileName),
            """{"source":"brew"}""");

        // 1. UpdateCommand self-update gate (BrewInstallDetection)
        Assert.Equal("brew upgrade aspire", BrewInstallDetection.GetBrewUpdateCommand(binaryPath));

        // 2. BundleService extract-dir router — must extract into the binary's
        // own directory so versions/<id>/ stays inside the Cellar prefix that
        // `brew uninstall aspire` will wipe.
        Assert.Equal(binaryDir.FullName, BundleService.ComputeDefaultExtractDir(binaryPath));

        // 3. CliPathHelper aspire-home install-route resolver — brew is NOT a
        // colocated-home route; the user's ASPIRE_HOME stays at ~/.aspire/
        // rather than under the brew prefix (so caches survive `brew upgrade`).
        // The resolver signals this by returning null and letting
        // GetAspireHomeDirectory fall through to the default.
        Assert.Null(CliPathHelper.TryGetAspireHomeDirectoryFromInstallRoute(binaryPath));
    }

    [Fact]
    public void BrewWireString_MatchesExpectedConstant()
    {
        // The wire string is part of the install-route contract documented in
        // docs/specs/install-routes.md and written verbatim by every brew
        // formula (eng/homebrew-core/aspire.rb.template). If anyone renames
        // the constant, the rename also has to be coordinated with the
        // shipped formula's sidecar write — the test fails so the change
        // can't land silently.
        Assert.Equal("brew", InstallSourceExtensions.BrewWire);
        Assert.Equal(InstallSource.Brew, InstallSourceExtensions.ParseInstallSource("brew"));
        Assert.Equal("brew", InstallSource.Brew.ToWireString());
    }

    [Fact]
    public void BrewSidecar_WrittenByFormulaTemplate_IsParseable()
    {
        // Mirror the exact byte sequence the formula writes
        // (eng/homebrew-core/aspire.rb.template), trailing newline and all.
        // This guards against the formula and the parser drifting apart on
        // whitespace, quoting style, or property ordering.
        using var temp = new TestTempDirectory();
        var sidecarPath = Path.Combine(temp.Path, InstallSidecarReader.SidecarFileName);
        File.WriteAllText(sidecarPath, "{\"source\":\"brew\"}\n");

        Assert.Equal("brew", InstallSidecarReader.ReadSourceField(sidecarPath));
    }
}
