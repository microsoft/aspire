// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using Aspire.Tray.Tests.Helpers;

namespace Aspire.Tray.Tests;

public class TraySavedStateTests
{
    [Fact]
    public void StateReloadPreservesPathsRecencyAndPinFlags()
    {
        using var directory = new TestTrayStateDirectory();
        var first = directory.CreateAppHost("first/apphost.cs");
        var second = directory.CreateAppHost("second/apphost.cs");
        var saved = TraySavedState.Empty.Remember(first).Remember(second).SetPinned(first, true);
        new FileTraySavedStateStore(directory.StatePath).Save(saved);

        var reloaded = new FileTraySavedStateStore(directory.StatePath).Load();

        Assert.Equal(saved.AppHosts, reloaded.AppHosts);
        using var json = JsonDocument.Parse(File.ReadAllText(directory.StatePath));
        Assert.Equal(["appHosts"], json.RootElement.EnumerateObject().Select(property => property.Name));
        Assert.All(json.RootElement.GetProperty("appHosts").EnumerateArray(), host =>
            Assert.Equal(["appHostPath", "isPinned", "isRecent"], host.EnumerateObject().Select(property => property.Name)));
        Assert.Equal([directory.StatePath], Directory.GetFiles(Path.GetDirectoryName(directory.StatePath)!));
    }

    [Fact]
    public void RecentHistoryIsBoundedWithoutEvictingPins()
    {
        var paths = Enumerable.Range(0, 30).Select(index => Path.GetFullPath($"project-{index}/apphost.cs")).ToArray();
        var saved = TraySavedState.Empty.Remember(paths[0]).SetPinned(paths[0], true);
        foreach (var path in paths.Skip(1))
        {
            saved = saved.Remember(path);
        }

        Assert.Equal(paths.TakeLast(20).Reverse(), saved.AppHosts.Where(host => host.IsRecent).Select(host => host.AppHostPath));
        Assert.Equal(new SavedAppHost(paths[0], true, false), saved.AppHosts.Single(host => host.IsPinned));
        Assert.Equal(21, saved.AppHosts.Count);

        saved = saved.Remember(paths[20]);
        Assert.Equal(paths[20], saved.AppHosts[0].AppHostPath);
        Assert.Equal(20, saved.AppHosts.Count(host => host.IsRecent));
        Assert.Equal(21, saved.AppHosts.Count);
    }

    [Fact]
    public void SavedStateUsesPlatformPathComparisonWithoutLosingPins()
    {
        using var directory = new TestTrayStateDirectory();
        var original = directory.CreateAppHost("project/apphost.cs");
        var differentlyCased = Path.Combine(Path.GetDirectoryName(original)!, "APPHOST.CS");
        var saved = TraySavedState.Empty.Remember(original).SetPinned(original, true).Remember(differentlyCased);

        SavedAppHost[] expected = OperatingSystem.IsWindows()
            ? [new(differentlyCased, true, true)]
            : [new(differentlyCased, false, true), new(original, true, true)];
        Assert.Equal(expected, saved.AppHosts);

        new FileTraySavedStateStore(directory.StatePath).Save(saved);
        Assert.Equal(expected, new FileTraySavedStateStore(directory.StatePath).Load().AppHosts);
        Assert.Equal(expected.Select(host => host with { IsPinned = false }).ToArray(),
            saved.SetPinned(original, false).AppHosts);
    }

    [Fact]
    public void ClearAndRemoveRecentPreservePinsIncludingMissingFiles()
    {
        using var directory = new TestTrayStateDirectory();
        var missing = Path.GetFullPath("deleted/apphost.cs");
        var recent = Path.GetFullPath("other/apphost.cs");
        var saved = TraySavedState.Empty.Remember(missing).SetPinned(missing, true).Remember(recent);

        Assert.Equal([new SavedAppHost(missing, true, false)], saved.ClearRecent().AppHosts);
        Assert.Equal([new SavedAppHost(recent, false, true), new SavedAppHost(missing, true, false)],
            saved.RemoveRecent(missing).AppHosts);

        var store = new FileTraySavedStateStore(directory.StatePath);
        store.Save(saved.ClearRecent());
        Assert.Equal([new SavedAppHost(missing, true, false)], new FileTraySavedStateStore(directory.StatePath).Load().AppHosts);
        Assert.Empty(saved.ClearRecent().SetPinned(missing, false).AppHosts);
    }

    [Fact]
    public void RemovingMissingPinsForgetsTheirHistoryWithoutRemovingUnpinnedRecents()
    {
        var pinned = Path.GetFullPath("pinned/apphost.cs");
        var recent = Path.GetFullPath("recent/apphost.cs");
        var retained = Path.GetFullPath("retained/apphost.cs");
        var state = TraySavedState.Empty.Remember(pinned).SetPinned(pinned, true)
            .Remember(recent).Remember(retained).SetPinned(retained, true);
        var missing = new HashSet<string>([pinned, recent], TrayAppHostPath.Comparer);

        Assert.Equal([new SavedAppHost(retained, true, true), new SavedAppHost(recent, false, true)],
            state.RemoveMissingPins(missing).AppHosts);
    }

    [Theory]
    [InlineData("{broken")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"appHosts\":null}")]
    [InlineData("{\"appHosts\":[null]}")]
    [InlineData("{\"appHosts\":[{\"appHostPath\":\"relative/apphost.cs\",\"isPinned\":true,\"isRecent\":false}]}")]
    [InlineData("{\"appHosts\":[],\"dashboardUrl\":\"https://localhost/?token=private\"}")]
    public void CorruptStateIsNeverOverwritten(string contents)
    {
        using var directory = new TestTrayStateDirectory();
        directory.WriteState(contents);
        var store = new FileTraySavedStateStore(directory.StatePath);

        var exception = Record.Exception(store.Load);
        Assert.True(exception is JsonException or InvalidDataException);
        Assert.Throws<InvalidDataException>(() => store.Save(TraySavedState.Empty.Remember(Path.GetFullPath("apphost.cs"))));
        Assert.Equal(contents, File.ReadAllText(directory.StatePath));
    }

    [Fact]
    public void SaveDetectsExternalChangesInsteadOfReplacingThem()
    {
        using var directory = new TestTrayStateDirectory();
        var store = new FileTraySavedStateStore(directory.StatePath);
        store.Save(TraySavedState.Empty);
        directory.WriteState("externally edited, incomplete state");

        Assert.Throws<IOException>(() => store.Save(TraySavedState.Empty.Remember(Path.GetFullPath("apphost.cs"))));
        Assert.Equal("externally edited, incomplete state", File.ReadAllText(directory.StatePath));
    }

    [Fact]
    public void RepeatedWritesReplaceCompleteStateAndLeaveNoTemporaryFiles()
    {
        using var directory = new TestTrayStateDirectory();
        var path = directory.CreateAppHost("apphost.cs");
        var store = new FileTraySavedStateStore(directory.StatePath);
        store.Save(TraySavedState.Empty.Remember(path));
        store.Save(TraySavedState.Empty.Remember(path).SetPinned(path, true));
        store.Save(TraySavedState.Empty);

        Assert.Empty(new FileTraySavedStateStore(directory.StatePath).Load().AppHosts);
        Assert.Equal([directory.StatePath], Directory.GetFiles(Path.GetDirectoryName(directory.StatePath)!));
    }

    [Fact]
    public void StateFileAndDirectoryArePrivateOnUnix()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        using var directory = new TestTrayStateDirectory();
        new FileTraySavedStateStore(directory.StatePath).Save(TraySavedState.Empty);

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(directory.StatePath));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
            File.GetUnixFileMode(Path.GetDirectoryName(directory.StatePath)!));
    }

    [Fact]
    public void StatePathMustBeAbsolute()
    {
        Assert.Throws<ArgumentException>(() => new FileTraySavedStateStore("history.json"));
        Assert.Throws<ArgumentException>(() => TrayAppHostPath.Normalize("relative/apphost.cs"));
    }

    [Fact]
    public void SourceProbesDistinguishExistingAndDanglingSymbolicLinks()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        using var directory = new TestTrayStateDirectory();
        var target = directory.CreateAppHost("target/apphost.cs");
        var link = Path.Combine(Path.GetDirectoryName(target)!, "linked.cs");
        File.CreateSymbolicLink(link, target);

        Assert.False(TrayAppHostPath.IsMissing(link));
        Assert.Equal(link, TrayAppHostPath.RequireExistingFile(link));
        File.Delete(target);
        Assert.True(TrayAppHostPath.IsMissing(link));
        Assert.Throws<FileNotFoundException>(() => TrayAppHostPath.RequireExistingFile(link));
    }
}
