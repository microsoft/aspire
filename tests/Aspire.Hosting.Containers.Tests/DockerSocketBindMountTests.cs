// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Utils;
using Aspire.TestUtilities;

namespace Aspire.Hosting.Containers.Tests;

public class DockerSocketBindMountTests(ITestOutputHelper testOutputHelper)
{
    [Fact]
    [RequiresFeature(TestFeature.Docker | TestFeature.DockerPluginBuildx)]
    public async Task WithDockerSocketBindMountAllowsDockerCliInContainer()
    {
        var dockerfile = """
            FROM netaspireci.azurecr.io/library/docker:28.1.1
            CMD sh -c "docker info > /out/docker-info.txt"
            """;

        using var dir = new TestTempDirectory();
        using var outDir = new TestTempDirectory();
        var dockerFilePath = Path.Combine(dir.Path, "Dockerfile");
        await File.WriteAllTextAsync(dockerFilePath, dockerfile);

        var appBuilder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        appBuilder.AddDockerfile("docker-client", contextPath: dir.Path)
                  .WithBindMount("/var/run/docker.sock", "/var/run/docker.sock")
                  .WithBindMount(outDir.Path, "/out");

        using var app = appBuilder.Build();

        await app.StartAsync();

        var rns = app.ResourceNotifications;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var state = await rns.WaitForResourceAsync(
            "docker-client",
            e => KnownResourceStates.TerminalStates.Contains(e.Snapshot.State?.Text),
            cts.Token);

        Assert.Equal(KnownResourceStates.Exited, state.Snapshot.State);

        var infoFile = Path.Combine(outDir.Path, "docker-info.txt");
        Assert.True(File.Exists(infoFile));

        var infoContent = await File.ReadAllTextAsync(infoFile);
        Assert.Contains("Server Version:", infoContent, StringComparison.OrdinalIgnoreCase);
    }
}