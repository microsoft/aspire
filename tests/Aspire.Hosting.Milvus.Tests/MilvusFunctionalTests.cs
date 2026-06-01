// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.TestUtilities;
using Aspire.Hosting.Tests.Utils;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Milvus.Client;

namespace Aspire.Hosting.Milvus.Tests;

public class MilvusFunctionalTests(ITestOutputHelper testOutputHelper)
{
    private const string CollectionName = "book";

    [Fact]
    [RequiresFeature(TestFeature.Docker)]
    public async Task VerifyMilvusResource()
    {
        using var builder = TestDistributedApplicationBuilder.CreateWithTestContainerRegistry(testOutputHelper);

        var milvus = builder.AddMilvus("milvus");
        var db = milvus.AddDatabase("milvusdb", "db1");

        using var app = builder.Build();

        await app.StartAsync();

        await app.WaitForTextAsync("Milvus Proxy successfully initialized and ready to serve", milvus.Resource.Name);

        var hb = Host.CreateApplicationBuilder();
        hb.AddTestLogging(testOutputHelper);

        hb.Configuration[$"ConnectionStrings:{db.Resource.Name}"] = await db.Resource.ConnectionStringExpression.GetValueAsync(default);

        hb.AddMilvusClient(db.Resource.Name);

        using var host = hb.Build();

        await host.StartAsync();

        var milvusClient = host.Services.GetRequiredService<MilvusClient>();

        await milvusClient.CreateDatabaseAsync("db1");
        await CreateTestDataAsync(milvusClient, default);
    }

    private static async Task CreateTestDataAsync(MilvusClient milvusClient, CancellationToken token)
    {
        var collection = await milvusClient.CreateCollectionAsync(
                CollectionName,
                [
                    FieldSchema.Create<long>("book_id", isPrimaryKey:true),
                    FieldSchema.Create<long>("word_count"),
                    FieldSchema.CreateVarchar("book_name", 256),
                    FieldSchema.CreateFloatVector("book_intro", 2)
                ]
            , cancellationToken: token);

        var collections = await milvusClient.ListCollectionsAsync(cancellationToken: token);
        Assert.Single(collections, c => c.Name == CollectionName);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [RequiresFeature(TestFeature.Docker)]
    public async Task WithDataShouldPersistStateBetweenUsages(bool useVolume)
    {
        var dbname = "milvusdbtest";

        string? volumeName = null;
        string? bindMountPath = null;

        try
        {
            using var builder1 = TestDistributedApplicationBuilder.CreateWithTestContainerRegistry(testOutputHelper);
            var milvus1 = builder1.AddMilvus("milvus1");
#pragma warning disable CS0618 // Type or member is obsolete
            var password = milvus1.Resource.ApiKeyParameter.Value;
#pragma warning restore CS0618 // Type or member is obsolete

            var db1 = milvus1.AddDatabase("milvusdb1", dbname);

            if (useVolume)
            {
                // Use a deterministic volume name to prevent them from exhausting the machines if deletion fails
                volumeName = VolumeNameGenerator.Generate(milvus1, nameof(WithDataShouldPersistStateBetweenUsages));

                // if the volume already exists (because of a crashing previous run), delete it
                DockerUtils.AttemptDeleteDockerVolume(volumeName, throwOnFailure: true);
                milvus1.WithDataVolume(volumeName);
            }
            else
            {
                bindMountPath = Path.Combine(Directory.CreateTempSubdirectory().FullName, "data");

                milvus1.WithDataBindMount(bindMountPath);
            }

            using (var app = builder1.Build())
            {
                await app.StartAsync();

                await app.WaitForTextAsync("Milvus Proxy successfully initialized and ready to serve", milvus1.Resource.Name);

                try
                {
                    var hb = Host.CreateApplicationBuilder();
                    hb.AddTestLogging(testOutputHelper);

                    hb.Configuration[$"ConnectionStrings:{db1.Resource.Name}"] = await db1.Resource.ConnectionStringExpression.GetValueAsync(default);

                    hb.AddMilvusClient(db1.Resource.Name);

                    using (var host = hb.Build())
                    {
                        await host.StartAsync();

                        var milvusClient = host.Services.GetRequiredService<MilvusClient>();

                        await milvusClient.CreateDatabaseAsync(dbname);

                        await CreateTestDataAsync(milvusClient, default);
                    }
                }
                finally
                {
                    // Stops the container, or the Volume would still be in use
                    await app.StopAsync();
                }
            }

            using var builder2 = TestDistributedApplicationBuilder.CreateWithTestContainerRegistry(testOutputHelper);
            var passwordParameter = builder2.AddParameter("pwd", password);

            var milvus2 = builder2.AddMilvus("milvus2", passwordParameter);
            var db2 = milvus2.AddDatabase("milvusdb2", dbname);

            if (useVolume)
            {
                milvus2.WithDataVolume(volumeName);
            }
            else
            {
                milvus2.WithDataBindMount(bindMountPath!);
            }

            using (var app = builder2.Build())
            {
                await app.StartAsync();

                await app.WaitForTextAsync("Milvus Proxy successfully initialized and ready to serve", milvus2.Resource.Name);

                try
                {
                    var hb = Host.CreateApplicationBuilder();
                    hb.AddTestLogging(testOutputHelper);

                    hb.Configuration[$"ConnectionStrings:{db2.Resource.Name}"] = await db2.Resource.ConnectionStringExpression.GetValueAsync(default);

                    hb.AddMilvusClient(db2.Resource.Name);

                    using (var host = hb.Build())
                    {
                        await host.StartAsync();

                        var milvusClient = host.Services.GetRequiredService<MilvusClient>();

                        var collections = await milvusClient.ListCollectionsAsync();

                        Assert.Single(collections, c => c.Name == CollectionName);
                    }
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
