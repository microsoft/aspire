// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Formats.Tar;
using System.IO.Compression;
using Aspire.Cli.Bundles;
using Aspire.Cli.Layout;
using Aspire.Cli.Tests.Utils;
using Aspire.Shared;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Cli.Tests.LayoutTests;

public sealed class TrayLayoutTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void DiscoveryKeepsTrayOptionalInLegacyAndBundleLayouts(bool bundleDirectory, bool hasTray)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(output);
        var root = workspace.Path;
        var components = bundleDirectory ? Path.Combine(root, "bundle") : root;
        Directory.CreateDirectory(Path.Combine(components, "managed"));
        Directory.CreateDirectory(Path.Combine(components, "dcp"));
        File.WriteAllText(Path.Combine(components, "managed", BundleDiscovery.GetExecutableFileName(BundleDiscovery.ManagedExecutableName)), "");
        File.WriteAllText(BundleDiscovery.GetDcpExecutablePath(Path.Combine(components, "dcp")), "");
        var trayPath = Path.Combine(components, OperatingSystem.IsWindows()
            ? WindowsTrayPayload.ExecutablePath : LayoutComponents.MacTrayExecutablePath);
        if (hasTray)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(trayPath)!);
            File.WriteAllText(trayPath, "");
        }
        var environment = new TestEnvironment(new Dictionary<string, string?>
        {
            [BundleDiscovery.LayoutPathEnvVar] = root
        });
        var discovery = new LayoutDiscovery(NullLogger<LayoutDiscovery>.Instance, environment);

        var layout = Assert.IsType<LayoutConfiguration>(discovery.DiscoverLayout());

        Assert.True(discovery.IsBundleModeAvailable());
        Assert.Equal(hasTray ? trayPath : null, layout.GetTrayPath());
        Assert.Equal(layout.GetTrayPath(), discovery.GetComponentPath(LayoutComponent.Tray));
    }

    [Fact]
    public void EmptyLayoutHasNoTray()
    {
        var layout = new LayoutConfiguration();

        Assert.Null(layout.Components.Tray);
        Assert.Null(layout.GetTrayPath());
    }

    [Theory]
    [InlineData("win-x64")]
    [InlineData("win-arm64")]
    public async Task WindowsTrayPayloadSurvivesProductionExtraction(string rid)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(output);
        var source = Path.Combine(workspace.Path, "publish");
        WindowsTrayTestPayload.Create(source, rid);
        using var payload = new MemoryStream();
        using (var gzip = new GZipStream(payload, CompressionMode.Compress, leaveOpen: true))
        using (var writer = new TarWriter(gzip, leaveOpen: true))
        {
            foreach (var file in new[] { "aspire-tray.exe", "Aspire.ico" })
            {
                writer.WriteEntry(Path.Combine(source, file), $"{rid}/tray/{file}");
            }
        }
        payload.Position = 0;
        var extracted = Path.Combine(workspace.Path, "extracted");

        await BundleService.ExtractPayloadAsync(payload, extracted, TestEnvironment.CreateWindows(), CancellationToken.None);

        WindowsTrayPayload.Validate(Path.Combine(extracted, "tray"), rid);
        Assert.Equal(new[] { "Aspire.ico", "aspire-tray.exe" }.Order(),
            Directory.GetFiles(Path.Combine(extracted, "tray")).Select(Path.GetFileName).Order());
        foreach (var file in new[] { "aspire-tray.exe", "Aspire.ico" })
        {
            Assert.Equal(File.ReadAllBytes(Path.Combine(source, file)), File.ReadAllBytes(Path.Combine(extracted, "tray", file)));
        }
    }
}
