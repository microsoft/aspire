// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Azure.Common;
using Xunit;

namespace Aspire.Azure.Storage.Files.DataLake.Tests;

public sealed class AzureDataLakeFileSystemSettingsTests
{
    private const string SampleConnectionString =
        "DefaultEndpointsProtocol=http;AccountName=devstoreaccount1;BlobEndpoint=http://127.0.0.1:10000/devstoreaccount1";

    [Fact]
    public void ParseConnectionStringInvalidInputThrowsArgumentException()
    {
        var settings = new AzureDataLakeFileSystemSettings();
        const string connectionString = "InvalidConnectionString";
        Assert.Throws<ArgumentException>(
            () => ((IConnectionStringSettings)settings).ParseConnectionString(connectionString));
    }

    [Theory]
    [InlineData("Endpoint=https://example.dfs.core.windows.net;FileSystemName=my-files")]
    [InlineData("Endpoint=https://example.dfs.core.windows.net;FileSystemName=my-files;ExtraParam=value")]
    [InlineData("endpoint=https://example.dfs.core.windows.net;filesystemname=my-files")]
    [InlineData("ENDPOINT=https://example.dfs.core.windows.net;FILESYSTEMNAME=my-files")]
    [InlineData("Endpoint=\"https://example.dfs.core.windows.net\";FileSystemName=\"my-files\"")]
    public void ParseConnectionStringWithServiceUri(string connectionString)
    {
        var settings = new AzureDataLakeFileSystemSettings();

        ((IConnectionStringSettings)settings).ParseConnectionString(connectionString);

        Assert.Equal("https://example.dfs.core.windows.net/", settings.ServiceUri?.ToString());
        Assert.Equal("my-files", settings.FileSystemName);
    }

    [Theory]
    [InlineData($"{SampleConnectionString};FileSystemName=my-files")]
    [InlineData($"{SampleConnectionString};FileSystemName=\"my-files\"")]
    public void ParseConnectionStringWithConnectionString(string connectionString)
    {
        var settings = new AzureDataLakeFileSystemSettings();

        ((IConnectionStringSettings)settings).ParseConnectionString(connectionString);

        Assert.Contains(SampleConnectionString, settings.ConnectionString, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("FileSystemName", settings.ConnectionString, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("my-files", settings.FileSystemName);
        Assert.Null(settings.ServiceUri);
    }

    [Theory]
    [InlineData("Endpoint=not-a-uri;FileSystemName=my-files")]
    public void ParseConnectionStringWithNotAUri(string connectionString)
    {
        var settings = new AzureDataLakeFileSystemSettings();

        ((IConnectionStringSettings)settings).ParseConnectionString(connectionString);

        Assert.True(string.IsNullOrEmpty(settings.ConnectionString));
        Assert.Equal("my-files", settings.FileSystemName);
        Assert.Null(settings.ServiceUri);
    }
}
