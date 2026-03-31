// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text;
using System.Text.Json;
using Aspire.TestUtilities;
using Aspire.Hosting.Tests.Utils;
using Aspire.Hosting.Utils;

namespace Aspire.Hosting.Seq.Tests;

public class SeqFunctionalTests(ITestOutputHelper testOutputHelper)
{
    [Fact]
    [RequiresFeature(TestFeature.Docker)]
    public async Task VerifySeqResource()
    {
        using var builder = TestDistributedApplicationBuilder.CreateWithTestContainerRegistry(testOutputHelper);

        var seq = builder.AddSeq("seq");

        using var app = builder.Build();

        await app.StartAsync();

        await app.WaitForTextAsync("Seq listening on", seq.Resource.Name);

        var seqUrl = await seq.Resource.ConnectionStringExpression.GetValueAsync(default);

        Assert.NotNull(seqUrl);

        var client = CreateClient(seqUrl);

        // Wait for the Seq resource to be healthy
        await app.ResourceNotifications.WaitForResourceHealthyAsync(seq.Resource.Name, default);

        await CreateTestDataAsync(client, default);
    }

    private static HttpClient CreateClient(string url)
    {
        HttpClient client = new()
        {
            BaseAddress = new Uri(url)
        };
        return client;
    }

    private static async Task CreateTestDataAsync(HttpClient httpClient, CancellationToken token)
    {
        var payload = """{"@t": "2025-02-07T12:00:00Z", "@l": "Information", "@mt": "User {Username} logged in.", "Username": "johndoe"}""";

        var content = new StringContent(payload, Encoding.UTF8, "application/json");

        var ingestResponse = await httpClient.PostAsync("/ingest/clef", content, token);
        ingestResponse.EnsureSuccessStatusCode();

        var response = await httpClient.GetAsync("/api/events?filter=Username='johndoe'", token);
        response.EnsureSuccessStatusCode();
        var reponseContent = await response.Content.ReadAsStringAsync(token);

        var jsonDocument = JsonDocument.Parse(reponseContent);
        var doc = jsonDocument.RootElement.EnumerateArray().FirstOrDefault();
        Assert.Equal("Information", doc.GetProperty("Level").GetString());

        var property = doc.GetProperty("Properties").EnumerateArray().FirstOrDefault();
        Assert.Equal("Username", property.GetProperty("Name").GetString());
        Assert.Equal("johndoe", property.GetProperty("Value").GetString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [RequiresFeature(TestFeature.Docker)]
    public async Task WithDataShouldPersistStateBetweenUsages(bool useVolume)
    {
        string? volumeName = null;
        string? bindMountPath = null;

        try
        {
            using var builder1 = TestDistributedApplicationBuilder.CreateWithTestContainerRegistry(testOutputHelper);
            var seq1 = builder1.AddSeq("seq1");

            if (useVolume)
            {
                // Use a deterministic volume name to prevent them from exhausting the machines if deletion fails
                volumeName = VolumeNameGenerator.Generate(seq1, nameof(WithDataShouldPersistStateBetweenUsages));

                // if the volume already exists (because of a crashing previous run), delete it
                DockerUtils.AttemptDeleteDockerVolume(volumeName, throwOnFailure: true);
                seq1.WithDataVolume(volumeName);
            }
            else
            {
                bindMountPath = Path.Combine(Directory.CreateTempSubdirectory().FullName, "data");
                seq1.WithDataBindMount(bindMountPath);
            }

            using (var app = builder1.Build())
            {
                await app.StartAsync();

                await app.WaitForTextAsync("Seq listening on", seq1.Resource.Name);

                try
                {
                    var seqUrl = await seq1.Resource.ConnectionStringExpression.GetValueAsync(default);

                    Assert.NotNull(seqUrl);

                    var client = CreateClient(seqUrl);

                    // Wait for the Seq resource to be healthy
                    await app.ResourceNotifications.WaitForResourceHealthyAsync(seq1.Resource.Name, default);

                    await CreateTestDataAsync(client, default);
                }
                finally
                {
                    // Stops the container, or the Volume would still be in use
                    await app.StopAsync();
                }
            }

            using var builder2 = TestDistributedApplicationBuilder.CreateWithTestContainerRegistry(testOutputHelper);

            var seq2 = builder2.AddSeq("seq2");

            if (useVolume)
            {
                seq2.WithDataVolume(volumeName);
            }
            else
            {
                seq2.WithDataBindMount(bindMountPath!);
            }

            using (var app = builder2.Build())
            {
                await app.StartAsync();

                await app.WaitForTextAsync("Seq listening on", seq2.Resource.Name);

                try
                {
                    var seqUrl = await seq2.Resource.ConnectionStringExpression.GetValueAsync(default);

                    Assert.NotNull(seqUrl);

                    var client = CreateClient(seqUrl);

                    // Wait for the Seq resource to be healthy
                    await app.ResourceNotifications.WaitForResourceHealthyAsync(seq2.Resource.Name, default);

                    await CreateTestDataAsync(client, default);
                }
                finally
                {
                    // Stops the container, or the Volume would still be in use
                    await app.StopAsync();
                }

            }

        }
        finally
        {
            if (volumeName is not null)
            {
                DockerUtils.AttemptDeleteDockerVolume(volumeName);
            }

            if (bindMountPath is not null)
            {
                try
                {
                    Directory.Delete(Path.GetDirectoryName(bindMountPath)!, recursive: true);
                }
                catch
                {
                    // Don't fail test if we can't clean the temporary folder
                }
            }
        }
    }
}
