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
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task LoadAndUpdate_UseSelectedProtectionMode(bool protect)
    {
        const string key = "Setting";
        var browser = new InMemoryBrowserLocalStorage();
        if (protect)
        {
            await browser.Storage.SetAsync<List<int>>(key, [1]);
        }
        else
        {
            await browser.Storage.SetUnprotectedAsync<List<int>>(key, [1]);
        }

        var setting = new LocalStorageSetting<List<int>>(browser.Storage, new LocalStorageLocks(), TimeProvider.System,
            key, NullLogger.Instance, protect, stored => stored ?? [], (x, y) => x.SequenceEqual(y), () => { });
        await setting.EnsureLoadedAsync().DefaultTimeout();
        Assert.Equal([1], setting.Read(value => value.ToArray()));

        Assert.True(setting.Update(value =>
        {
            if (value.Contains(2))
            {
                return false;
            }

            value.Add(2);
            return true;
        }));

        var saved = protect
            ? await browser.Storage.GetAsync<List<int>>(key)
            : await browser.Storage.GetUnprotectedAsync<List<int>>(key);
        Assert.True(saved.Success);
        Assert.Equal([1, 2], saved.Value);
    }
}