// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Model.BrowserStorage;
using Aspire.Dashboard.Tests.Shared;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Aspire.Dashboard.Tests.Model;

public sealed class LocalStorageSettingTests
{
    private const string Key = "Setting";

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task LoadAndUpdate_PublishesOnlyAfterStorageConfirmsSave(bool protect)
    {
        var browser = new InMemoryBrowserLocalStorage();
        if (protect)
        {
            await browser.Storage.SetAsync<List<int>>(Key, [1]);
        }
        else
        {
            await browser.Storage.SetUnprotectedAsync<List<int>>(Key, [1]);
        }

        var changes = 0;
        var setting = CreateSetting(browser, protect, () => changes++);
        await setting.EnsureLoadedAsync().DefaultTimeout();
        Assert.Equal([1], setting.Read(value => value.ToArray()));

        var writeCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var acknowledgeWrite = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        browser.OnAfterWriteAsync = () =>
        {
            writeCompleted.SetResult();
            return acknowledgeWrite.Task;
        };
        var save = AddAsync(setting, 2);
        try
        {
            await writeCompleted.Task.DefaultTimeout();
            Assert.False(save.IsCompleted);
            Assert.Equal([1], setting.Read(value => value.ToArray()));
            Assert.Equal(1, changes);
        }
        finally
        {
            acknowledgeWrite.SetResult();
        }

        Assert.Equal(SettingUpdateResult.Changed, await save.DefaultTimeout());
        Assert.Equal([1, 2], setting.Read(value => value.ToArray()));
        Assert.Equal(2, changes);
        var saved = protect
            ? await browser.Storage.GetAsync<List<int>>(Key)
            : await browser.Storage.GetUnprotectedAsync<List<int>>(Key);
        Assert.True(saved.Success);
        Assert.Equal([1, 2], saved.Value);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Update_FailedWriteDoesNotPublishOrCarryOverToTheNextSave(bool protect)
    {
        var browser = new InMemoryBrowserLocalStorage();
        var changes = 0;
        var setting = CreateSetting(browser, protect, () => changes++);
        Assert.Equal(SettingUpdateResult.Changed, await AddAsync(setting, 1));
        var saved = browser.Items[Key];

        browser.FailWrites = true;
        Assert.Equal(SettingUpdateResult.NotSaved, await AddAsync(setting, 2));
        Assert.Equal([1], setting.Read(value => value.ToArray()));
        Assert.Equal(saved, browser.Items[Key]);
        Assert.Equal(1, changes);

        browser.FailWrites = false;
        Assert.Equal(SettingUpdateResult.Changed, await AddAsync(setting, 3));
        Assert.Equal([1, 3], setting.Read(value => value.ToArray()));
        var reloaded = CreateSetting(browser, protect, () => { });
        await reloaded.EnsureLoadedAsync().DefaultTimeout();
        Assert.Equal([1, 3], reloaded.Read(value => value.ToArray()));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("invalid protected value")]
    public async Task Update_NoOpRefreshesStaleValueWithoutWriting(string? storedValue)
    {
        var browser = new InMemoryBrowserLocalStorage();
        await browser.Storage.SetAsync<List<int>>(Key, [1]);
        var changes = 0;
        var setting = CreateSetting(browser, protect: true, () => changes++);
        await setting.EnsureLoadedAsync().DefaultTimeout();
        Assert.Equal([1], setting.Read(value => value.ToArray()));

        if (storedValue is null)
        {
            browser.Items.Remove(Key);
        }
        else
        {
            browser.Items[Key] = storedValue;
        }

        browser.FailWrites = true;
        var result = await setting.UpdateAsync(value => value.Remove(1) ? SettingUpdateResult.Changed : SettingUpdateResult.Unchanged);

        Assert.Equal(SettingUpdateResult.Unchanged, result);
        Assert.Empty(setting.Read(value => value.ToArray()));
        Assert.Equal(2, changes);
        Assert.Equal(storedValue, browser.Items.GetValueOrDefault(Key));
    }

    private static LocalStorageSetting<List<int>> CreateSetting(InMemoryBrowserLocalStorage browser, bool protect, Action onChanged) =>
        new(browser.Storage, new LocalStorageLocks(), Key, NullLogger.Instance, protect, stored => stored?.ToList() ?? [], (x, y) => x.SequenceEqual(y), onChanged);

    private static Task<SettingUpdateResult> AddAsync(LocalStorageSetting<List<int>> setting, int item) =>
        setting.UpdateAsync(value =>
        {
            value.Add(item);
            return SettingUpdateResult.Changed;
        });
}