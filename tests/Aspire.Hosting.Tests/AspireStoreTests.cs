// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Utils;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting.Tests;

[Trait("Partition", "5")]
public class AspireStoreTests
{
    [Fact]
    public void Create_ShouldInitializeStore()
    {
        var store = CreateStore();

        Assert.NotNull(store);
        Assert.True(Directory.Exists(Path.GetDirectoryName(store.BasePath)));
    }

    [Fact]
    public void BasePath_ShouldBeAbsolute()
    {
        var store = CreateStore();

        var path = store.BasePath;

        Assert.True(Path.IsPathRooted(path));
    }

    [Fact]
    public void BasePath_ShouldUseConfiguration()
    {
        var builder = TestDistributedApplicationBuilder.Create();
        builder.Configuration[AspireStore.AspireStorePathKeyName] = Path.GetTempPath();
        var app = builder.Build();
        var store = app.Services.GetRequiredService<IAspireStore>();

        var path = store.BasePath;

        Assert.DoesNotContain($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", path);
        Assert.Contains(Path.GetTempPath(), path);
    }

    [Fact]
    public void BasePath_ShouldBePrefixed_WhenUsingConfiguration()
    {
        var store = CreateStore();

        var path = store.BasePath;

        Assert.Contains(".aspire", path);
    }

    [Fact]
    public void GetOrCreateFileWithContent_ShouldCreateFile_WithStreamContent()
    {
        var builder = TestDistributedApplicationBuilder.Create();
        builder.Configuration[AspireStore.AspireStorePathKeyName] = Path.GetTempPath();
        var app = builder.Build();
        var store = app.Services.GetRequiredService<IAspireStore>();

        var filename = "testfile2.txt";
        var content = new MemoryStream(System.Text.Encoding.UTF8.GetBytes("Test content"));
        var filePath = store.GetFileNameWithContent(filename, content);

        Assert.True(File.Exists(filePath));
        Assert.Equal("Test content", File.ReadAllText(filePath));
    }

    [Fact]
    public void GetOrCreateFileWithContent_ShouldCreateFile_WithFileContent()
    {
        var store = CreateStore();

        var filename = "testfile2.txt";
        var tempFilename = Path.GetTempFileName();
        File.WriteAllText(tempFilename, "Test content");
        var filePath = store.GetFileNameWithContent(filename, tempFilename);

        Assert.True(File.Exists(filePath));
        Assert.Equal("Test content", File.ReadAllText(filePath));

        try
        {
            File.Delete(tempFilename);
        }
        catch
        {
        }
    }

    [Fact]
    public void GetOrCreateFileWithContent_Throws_WhenSourceDoesntExist()
    {
        var store = CreateStore();

        Assert.Throws<FileNotFoundException>(() => store.GetFileNameWithContent("testfile.txt", "randomfilename.txt"));
    }

    [Fact]
    public void GetOrCreateFileWithContent_ShouldNotRecreateFile()
    {
        var store = CreateStore();

        var filename = "testfile3.txt";
        var content = new MemoryStream(System.Text.Encoding.UTF8.GetBytes("Test content"));
        var filePath = store.GetFileNameWithContent(filename, content);

        File.WriteAllText(filePath, "updated");

        content.Position = 0;
        var filePath2 = store.GetFileNameWithContent(filename, content);
        var content2 = File.ReadAllText(filePath2);

        Assert.Equal("updated", content2);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("./folder")]
    [InlineData(".\\folder")]
    [InlineData("folder")]
    [InlineData("obj/")]
    [InlineData("obj\\")]
    public void AspireStoreConstructor_ShouldThrow_IfNotAbsolutePath(string? basePath)
    {
        var directoryService = new FileSystemService(new ConfigurationBuilder().Build());
        Assert.ThrowsAny<Exception>(() => new AspireStore(basePath!, directoryService));
    }

    private static IAspireStore CreateStore()
    {
        var builder = TestDistributedApplicationBuilder.Create();
        builder.Configuration[AspireStore.AspireStorePathKeyName] = Path.GetTempPath();
        var app = builder.Build();
        return app.Services.GetRequiredService<IAspireStore>();
    }
}
