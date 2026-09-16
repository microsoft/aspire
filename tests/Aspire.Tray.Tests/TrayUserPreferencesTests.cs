// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using Aspire.Tray.Tests.Helpers;

namespace Aspire.Tray.Tests;

public class TrayUserPreferencesTests
{
    [Fact]
    public void PreferencesAreSavedUnderTheUserProfile()
    {
        using var directory = new TestTrayStateDirectory();
        var store = FileTraySavedStateStore.CreateWithLegacyMigration(directory.UserStatePath, directory.LegacyDirectory);
        store.Save(TraySavedState.Empty with { ConfirmStop = false });

        Assert.False(new FileTraySavedStateStore(directory.UserStatePath).Load().ConfirmStop);
    }

    [Fact]
    public void ExistingPreferencesAreImportedWithoutChangingTheOriginal()
    {
        using var directory = new TestTrayStateDirectory();
        var legacyPath = Path.Combine(directory.LegacyDirectory, "apphosts.json");
        var host = directory.CreateAppHost("shop/apphost.cs");
        var saved = new TraySavedState([new(host, true, true)]) { ConfirmStop = false };
        new FileTraySavedStateStore(legacyPath).Save(saved);
        var original = File.ReadAllBytes(legacyPath);

        var loaded = FileTraySavedStateStore.CreateWithLegacyMigration(directory.UserStatePath, directory.LegacyDirectory).Load();

        Assert.Equal(saved.AppHosts, loaded.AppHosts);
        Assert.False(loaded.ConfirmStop);
        Assert.Equal(original, File.ReadAllBytes(legacyPath));
        Assert.Equal(saved.AppHosts, new FileTraySavedStateStore(directory.UserStatePath).Load().AppHosts);
    }

    [Fact]
    public void ExistingUserFileTakesPrecedenceOverLegacyPreferences()
    {
        using var directory = new TestTrayStateDirectory();
        new FileTraySavedStateStore(Path.Combine(directory.LegacyDirectory, "apphosts.json"))
            .Save(TraySavedState.Empty with { ConfirmStop = false });
        new FileTraySavedStateStore(directory.UserStatePath).Save(TraySavedState.Empty);

        var loaded = FileTraySavedStateStore.CreateWithLegacyMigration(directory.UserStatePath, directory.LegacyDirectory).Load();

        Assert.True(loaded.ConfirmStop);
        Assert.Empty(loaded.AppHosts);
    }

    [Fact]
    public void InvalidLegacyPreferencesAreNotSilentlyReplaced()
    {
        using var directory = new TestTrayStateDirectory();
        Directory.CreateDirectory(directory.LegacyDirectory);
        var legacyPath = Path.Combine(directory.LegacyDirectory, "apphosts.json");
        File.WriteAllText(legacyPath, "invalid json");
        var store = FileTraySavedStateStore.CreateWithLegacyMigration(directory.UserStatePath, directory.LegacyDirectory);

        Assert.Throws<JsonException>(store.Load);
        Assert.Throws<InvalidDataException>(() => store.Save(TraySavedState.Empty));
        Assert.Equal("invalid json", File.ReadAllText(legacyPath));
        Assert.False(File.Exists(directory.UserStatePath));
    }
}
