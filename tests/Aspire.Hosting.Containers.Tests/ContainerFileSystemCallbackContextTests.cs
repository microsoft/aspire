// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting.Containers.Tests;

public class ContainerFileSystemCallbackContextTests
{
    private static ContainerFileSystemCallbackContext CreateContext()
    {
        return new ContainerFileSystemCallbackContext
        {
            Model = new ContainerResource("test"),
            Services = new ServiceCollection().BuildServiceProvider(),
        };
    }

    [Fact]
    public void CreateFileProducesContainerFileWithInlineContents()
    {
        var context = CreateContext();

        var item = context.CreateFile("app.conf", contents: "key=value", mode: 420 /* 0o644 */, owner: 1000, group: 1000, continueOnError: true);

        var file = Assert.IsType<ContainerFile>(item);
        Assert.Equal("app.conf", file.Name);
        Assert.Equal("key=value", file.Contents);
        Assert.Null(file.SourcePath);
        Assert.Equal((UnixFileMode)420 /* 0o644 */, file.Mode);
        Assert.Equal(1000, file.Owner);
        Assert.Equal(1000, file.Group);
        Assert.True(file.ContinueOnError);
    }

    [Fact]
    public void CreateFileProducesContainerFileWithSourcePath()
    {
        var context = CreateContext();

        var item = context.CreateFile("app.conf", sourcePath: "/host/app.conf");

        var file = Assert.IsType<ContainerFile>(item);
        Assert.Equal("/host/app.conf", file.SourcePath);
        Assert.Null(file.Contents);
        // A null mode is converted to UnixFileMode 0, which means "inherit".
        Assert.Equal((UnixFileMode)0, file.Mode);
    }

    [Fact]
    public void CreateCertificateFileProducesContainerOpenSSLCertificateFile()
    {
        var context = CreateContext();

        var item = context.CreateCertificateFile("server.pem", contents: "-----BEGIN CERTIFICATE-----");

        var cert = Assert.IsType<ContainerOpenSSLCertificateFile>(item);
        Assert.Equal("server.pem", cert.Name);
        Assert.Equal("-----BEGIN CERTIFICATE-----", cert.Contents);
    }

    [Fact]
    public void CreateDirectoryProducesContainerDirectoryWithEntries()
    {
        var context = CreateContext();

        var child = context.CreateFile("nested.conf", contents: "nested=true");
        var item = context.CreateDirectory("conf.d", [child], mode: 493 /* 0o755 */);

        var directory = Assert.IsType<ContainerDirectory>(item);
        Assert.Equal("conf.d", directory.Name);
        Assert.Equal((UnixFileMode)493 /* 0o755 */, directory.Mode);
        var entry = Assert.Single(directory.Entries);
        Assert.Same(child, entry);
    }

    [Fact]
    public void CreateDirectoryMaterializesLazyEntries()
    {
        var context = CreateContext();

        var evaluationCount = 0;
        IEnumerable<ContainerFileSystemItem> LazyEntries()
        {
            evaluationCount++;
            yield return context.CreateFile("nested.conf", contents: "nested=true");
        }

        var item = context.CreateDirectory("conf.d", LazyEntries());

        // The directory factory eagerly materializes the sequence so a lazily-resolved
        // handle sequence is captured at call time rather than on later enumeration.
        Assert.Equal(1, evaluationCount);
        var directory = Assert.IsType<ContainerDirectory>(item);
        Assert.Single(directory.Entries);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0x1000)]
    public void CreateFileWithOutOfRangeModeThrows(int mode)
    {
        var context = CreateContext();

        Assert.Throws<ArgumentOutOfRangeException>(() => context.CreateFile("app.conf", mode: mode));
    }

    [Fact]
    public void CreateFileWithMaximumModeSucceeds()
    {
        var context = CreateContext();

        var item = context.CreateFile("app.conf", mode: 4095 /* 0o7777 */);

        var file = Assert.IsType<ContainerFile>(item);
        Assert.Equal((UnixFileMode)4095 /* 0o7777 */, file.Mode);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void CreateFileWithInvalidNameThrows(string name)
    {
        var context = CreateContext();

        Assert.Throws<ArgumentException>(() => context.CreateFile(name));
    }

    [Fact]
    public void CreateDirectoryWithNullEntriesThrows()
    {
        var context = CreateContext();

        Assert.Throws<ArgumentNullException>(() => context.CreateDirectory("conf.d", entries: null!));
    }

    [Fact]
    public void CreateFileWithBothContentsAndSourcePathThrows()
    {
        var context = CreateContext();

        Assert.Throws<ArgumentException>(() => context.CreateFile("app.conf", contents: "key=value", sourcePath: "/host/app.conf"));
    }

    [Fact]
    public void CreateCertificateFileWithBothContentsAndSourcePathThrows()
    {
        var context = CreateContext();

        Assert.Throws<ArgumentException>(() => context.CreateCertificateFile("server.pem", contents: "-----BEGIN CERTIFICATE-----", sourcePath: "/host/server.pem"));
    }
}
