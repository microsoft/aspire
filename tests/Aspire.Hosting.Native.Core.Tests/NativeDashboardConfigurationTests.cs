// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using Aspire.Hosting.Native.Dashboard;
using Aspire.Hosting.Native.Dcp;

namespace Aspire.Hosting.Native.Core.Tests;

public class NativeDashboardConfigurationTests
{
    [Theory]
    [InlineData("dashboard.dll")]
    [InlineData("dashboard")]
    [InlineData("aspire-managed.dll")]
    [InlineData("aspire-managed")]
    [InlineData("aspire-managed.exe")]
    public async Task DashboardLaunchModesKeepTheirCredentialAndPortProjection(string fileName)
    {
        var directory = Directory.CreateTempSubdirectory("aspire-dashboard-config-");
        try
        {
            var path = Path.Combine(directory.FullName, fileName);
            await File.WriteAllBytesAsync(path, [], TestContext.Current.CancellationToken);
            var spec = DcpDashboardConfiguration.Create(path, "frontend", new("http://127.0.0.1:12345"),
                new("resource-key", "browser-token", "dashboard-key", "otlp-key"));
            await Verifier.Verify(JsonSerializer.Serialize(spec, DcpJsonContext.Default.DcpWorkloadSpec), "json")
                .UseDirectory("Snapshots").UseParameters(fileName)
                .AddScrubber(text => text.Replace(JsonEncodedText.Encode(path).ToString(), "/dashboard/" + fileName)
                    .Replace(JsonEncodedText.Encode(directory.FullName).ToString(), "/dashboard"));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void DashboardConfigurationRejectsRelativePathsBeforeLaunch() =>
        Assert.Throws<ArgumentException>(() => DcpDashboardConfiguration.Create("dashboard.dll", "frontend",
            new("http://127.0.0.1:12345"), new("resource-key", "browser-token", "dashboard-key", "otlp-key")));
}
