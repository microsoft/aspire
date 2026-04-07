// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Tests;

[Trait("Partition", "2")]
public class ValueSnapshotTests
{
    [Fact]
    public async Task GetValueAsync_WaitsForFirstValue()
    {
        var snapshot = new ValueSnapshot<string>();

        var getTask = snapshot.GetValueAsync();
        Assert.False(getTask.IsCompleted);

        snapshot.SetValue("test");

        var result = await getTask;
        Assert.Equal("test", result);
    }

    [Fact]
    public async Task GetValueAsync_ReturnsImmediatelyAfterValueSet()
    {
        var snapshot = new ValueSnapshot<string>();
        snapshot.SetValue("test");

        var getTask = snapshot.GetValueAsync();

        Assert.True(getTask.IsCompleted);

        var result = await getTask;

        Assert.Equal("test", result);
    }

    [Fact]
    public async Task SetValue_UpdatesValue()
    {
        var snapshot = new ValueSnapshot<string>();
        snapshot.SetValue("first");

        var firstResult = await snapshot.GetValueAsync();
        Assert.Equal("first", firstResult);

        snapshot.SetValue("second");

        var secondResult = await snapshot.GetValueAsync();
        Assert.Equal("second", secondResult);
    }

    [Fact]
    public async Task SetValue_CompletesAllWaiters()
    {
        var snapshot = new ValueSnapshot<string>();

        var task1 = snapshot.GetValueAsync();
        var task2 = snapshot.GetValueAsync();
        var task3 = snapshot.GetValueAsync();

        snapshot.SetValue("test");

        var result1 = await task1;
        var result2 = await task2;
        var result3 = await task3;

        Assert.Equal("test", result1);
        Assert.Equal("test", result2);
        Assert.Equal("test", result3);
    }

    [Fact]
    public async Task SetException_ThrowsException()
    {
        var snapshot = new ValueSnapshot<string>();
        var exception = new InvalidOperationException("Test error");

        snapshot.SetException(exception);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => snapshot.GetValueAsync());
        Assert.Same(exception, ex);
    }

    [Fact]
    public async Task SetException_CompletesAllWaiters()
    {
        var snapshot = new ValueSnapshot<string>();
        var exception = new InvalidOperationException("Test error");

        var task1 = snapshot.GetValueAsync();
        var task2 = snapshot.GetValueAsync();

        snapshot.SetException(exception);

        var ex1 = await Assert.ThrowsAsync<InvalidOperationException>(() => task1);
        var ex2 = await Assert.ThrowsAsync<InvalidOperationException>(() => task2);

        Assert.Same(exception, ex1);
        Assert.Same(exception, ex2);
    }

    [Fact]
    public async Task SetException_CanBeReplacedWithValue()
    {
        var snapshot = new ValueSnapshot<string>();
        var exception = new InvalidOperationException("Test error");

        snapshot.SetException(exception);
        await Assert.ThrowsAsync<InvalidOperationException>(() => snapshot.GetValueAsync());

        snapshot.SetValue("recovered");

        var result = await snapshot.GetValueAsync();
        Assert.Equal("recovered", result);
    }

    [Fact]
    public async Task SetValue_CanBeReplacedWithException()
    {
        var snapshot = new ValueSnapshot<string>();

        snapshot.SetValue("test");
        Assert.Equal("test", await snapshot.GetValueAsync());

        var exception = new InvalidOperationException("Error occurred");
        snapshot.SetException(exception);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => snapshot.GetValueAsync());
        Assert.Same(exception, ex);
    }

    [Fact]
    public void IsValueSet_FalseBeforeAnySet()
    {
        var snapshot = new ValueSnapshot<string>();

        Assert.False(snapshot.IsValueSet);
    }

    [Fact]
    public void IsValueSet_TrueAfterSetValue()
    {
        var snapshot = new ValueSnapshot<string>();

        snapshot.SetValue("test");

        Assert.True(snapshot.IsValueSet);
    }

    [Fact]
    public void IsValueSet_TrueAfterSetException()
    {
        var snapshot = new ValueSnapshot<string>();

        snapshot.SetException(new InvalidOperationException());

        Assert.True(snapshot.IsValueSet);
    }

    [Fact]
    public void IsValueSet_RemainsTrueAfterUpdate()
    {
        var snapshot = new ValueSnapshot<string>();

        snapshot.SetValue("first");
        Assert.True(snapshot.IsValueSet);

        snapshot.SetValue("second");
        Assert.True(snapshot.IsValueSet);

        snapshot.SetException(new InvalidOperationException());
        Assert.True(snapshot.IsValueSet);
    }

    [Fact]
    public async Task GetValueAsync_SupportsCancellation()
    {
        var snapshot = new ValueSnapshot<string>();
        using var cts = new CancellationTokenSource();

        var getTask = snapshot.GetValueAsync(cts.Token);
        cts.Cancel();

        await Assert.ThrowsAsync<TaskCanceledException>(() => getTask);
    }

    [Fact]
    public async Task GetValueAsync_CancellationDoesNotAffectOtherWaiters()
    {
        var snapshot = new ValueSnapshot<string>();
        using var cts = new CancellationTokenSource();

        var cancelledTask = snapshot.GetValueAsync(cts.Token);
        var normalTask = snapshot.GetValueAsync();

        cts.Cancel();
        await Assert.ThrowsAsync<TaskCanceledException>(() => cancelledTask);

        snapshot.SetValue("test");
        var result = await normalTask;

        Assert.Equal("test", result);
    }

    [Fact]
    public void SetException_ThrowsArgumentNullException_WhenExceptionIsNull()
    {
        var snapshot = new ValueSnapshot<string>();

        Assert.Throws<ArgumentNullException>(() => snapshot.SetException(null!));
    }
}
