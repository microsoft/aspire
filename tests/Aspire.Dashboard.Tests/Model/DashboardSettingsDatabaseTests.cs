// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Model;
using Aspire.Dashboard.Otlp.Model;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Aspire.Dashboard.Tests.Model;

public sealed class DashboardSettingsDatabaseTests(ITestOutputHelper testOutputHelper) : IDisposable
{
    private readonly TemporaryWorkspace _workspace = TemporaryWorkspace.Create(testOutputHelper);

    [Fact]
    public void Load_UnreadableDatabase_KeepsChangesInMemoryWithoutWriting()
    {
        // A directory where the database file should be makes opening it fail.
        var databasePath = Path.Combine(_workspace.Path, DashboardSettingsDatabase.DatabaseFileName);
        Directory.CreateDirectory(databasePath);
        var database = new DashboardSettingsDatabase(databasePath, NullLogger.Instance);

        var loaded = false;
        database.Load("test settings", _ => loaded = true);
        var saved = false;
        var result = database.TrySave("test settings", _ => saved = true);

        Assert.False(loaded);
        Assert.True(result);
        Assert.False(saved);
    }

    [Fact]
    public void TrySave_FailedWrite_ReturnsFalse()
    {
        var databasePath = Path.Combine(_workspace.Path, DashboardSettingsDatabase.DatabaseFileName);
        var database = new DashboardSettingsDatabase(databasePath, NullLogger.Instance);
        database.Load("test settings", _ => { });

        var result = database.TrySave("test settings", connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO missing_table (value) VALUES (1);";
            command.ExecuteNonQuery();
        });

        Assert.False(result);
    }

    [Fact]
    public void Stores_UnreadableDatabase_AllKeepChangesInMemory()
    {
        var databasePath = Path.Combine(_workspace.Path, DashboardSettingsDatabase.DatabaseFileName);
        Directory.CreateDirectory(databasePath);
        var tags = new ResourceTagStore(databasePath, NullLogger.Instance);
        var order = new ResourceOrderStore(databasePath, NullLogger.Instance);
        var pins = new MetricPinStore(databasePath, NullLogger.Instance);
        var instrument = new OtlpInstrumentKey("System.Runtime", "dotnet.gc.heap.total_allocated");

        Assert.True(tags.AddTag("api", "backend"));
        order.EnsureResources(["api_0", "cache_0"]);
        Assert.True(order.TryMove("cache_0", "api_0", after: false));
        Assert.True(pins.SetPinned(instrument, pinned: true));

        Assert.Equal(["backend"], tags.GetTags("api"));
        Assert.Equal(["cache_0", "api_0"], order.GetPositions().OrderBy(pair => pair.Value).Select(pair => pair.Key));
        Assert.Equal([instrument], pins.GetPins());
        Assert.Empty(Directory.GetFileSystemEntries(databasePath));
    }

    public void Dispose() => _workspace.Dispose();
}
