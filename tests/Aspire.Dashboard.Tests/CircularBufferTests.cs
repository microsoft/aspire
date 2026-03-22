// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Xunit;

namespace Aspire.Dashboard.Tests;

public class CircularBufferTests
{
    private static CircularBuffer<string> CreateBuffer(int capacity) => new(capacity);

    [Fact]
    public void LargeData_AddWhenFull_KeepOrder()
    {
        // The data here was reproduced from this issue: https://github.com/microsoft/aspire/issues/7854
        var values = new long[10_000];
        for (var i = 0; i < values.Length; i++)
        {
            var index = (521 + i) % values.Length;
            values[index] = i * 2;
        }

        var buffer = new CircularBuffer<long>(values.ToList(), 10_000, 521, 521);

        AssertOrder(buffer);

        var newValue = 18973;

        for (var i = buffer.Count - 1; i >= 0; i--)
        {
            var currentTrace = buffer[i];
            if (newValue > currentTrace)
            {
                buffer.Insert(i + 1, newValue);
                break;
            }
        }

        AssertOrder(buffer);

        static void AssertOrder(CircularBuffer<long> buffer)
        {
            long current = default;
            for (var i = 0; i < buffer.Count; i++)
            {
                var value = buffer[i];
                if (value < current)
                {
                    throw new InvalidOperationException($"Traces not in order at index {i}. {value} shouldn't be smaller than {current}.");
                }

                current = value;
            }
        }
    }

    [Fact]
    public void AddUntilFull()
    {
        var b = CreateBuffer(5);

        b.Add("0");
        b.Add("1");
        b.Add("2");
        b.Add("3");
        b.Add("4");
        b.Add("5");
        b.Add("6");

        Assert.Collection(b,
            i => Assert.Equal("2", i),
            i => Assert.Equal("3", i),
            i => Assert.Equal("4", i),
            i => Assert.Equal("5", i),
            i => Assert.Equal("6", i));

        Assert.Collection(b._buffer,
            i => Assert.Equal("5", i),
            i => Assert.Equal("6", i),
            i => Assert.Equal("2", i),
            i => Assert.Equal("3", i),
            i => Assert.Equal("4", i));
        Assert.Equal(2, b._start);
        Assert.Equal(2, b._end);
    }

    [Fact]
    public void InsertAtZeroUntilFull()
    {
        var b = CreateBuffer(5);

        b.Insert(0, "0");
        b.Insert(0, "1");
        b.Insert(0, "2");
        b.Insert(0, "3");
        b.Insert(0, "4");
        b.Insert(0, "5");
        b.Insert(0, "6");

        Assert.Collection(b,
            i => Assert.Equal("4", i),
            i => Assert.Equal("3", i),
            i => Assert.Equal("2", i),
            i => Assert.Equal("1", i),
            i => Assert.Equal("0", i));
    }

    [Fact]
    public void InsertAtEndUntilFull()
    {
        var b = CreateBuffer(5);

        b.Insert(0, "0");
        b.Insert(1, "1");
        b.Insert(2, "2");
        b.Insert(3, "3");
        b.Insert(4, "4");
        b.Insert(5, "5");
        b.Insert(5, "6");

        Assert.Collection(b,
            i => Assert.Equal("2", i),
            i => Assert.Equal("3", i),
            i => Assert.Equal("4", i),
            i => Assert.Equal("5", i),
            i => Assert.Equal("6", i));
    }

    [Fact]
    public void InsertAtPositionUntilFull()
    {
        var b = CreateBuffer(10);

        b.Insert(0, "1");
        b.Insert(1, "2");
        b.Insert(2, "3");
        b.Insert(3, "10");
        b.Insert(3, "9");
        b.Insert(3, "4");
        b.Insert(4, "5");
        b.Insert(5, "7");
        b.Insert(5, "6");
        b.Insert(7, "8");

        Assert.Collection(b,
            i => Assert.Equal("1", i),
            i => Assert.Equal("2", i),
            i => Assert.Equal("3", i),
            i => Assert.Equal("4", i),
            i => Assert.Equal("5", i),
            i => Assert.Equal("6", i),
            i => Assert.Equal("7", i),
            i => Assert.Equal("8", i),
            i => Assert.Equal("9", i),
            i => Assert.Equal("10", i));
    }

    [Fact]
    public void InsertInMiddleWhileFull()
    {
        var b = CreateBuffer(10);

        b.Add("0");
        b.Add("1");
        b.Add("2");
        b.Add("3");
        b.Add("4");
        b.Add("5");
        b.Add("6");
        b.Add("7");
        b.Add("8");
        b.Add("9");
        b.Add("10");
        b.Add("11");

        b.Insert(3, "4.5");

        Assert.Collection(b,
            i => Assert.Equal("3", i),
            i => Assert.Equal("4", i),
            i => Assert.Equal("4.5", i),
            i => Assert.Equal("5", i),
            i => Assert.Equal("6", i),
            i => Assert.Equal("7", i),
            i => Assert.Equal("8", i),
            i => Assert.Equal("9", i),
            i => Assert.Equal("10", i),
            i => Assert.Equal("11", i));

        Assert.Collection(b._buffer,
            i => Assert.Equal("9", i),
            i => Assert.Equal("10", i),
            i => Assert.Equal("11", i),
            i => Assert.Equal("3", i),
            i => Assert.Equal("4", i),
            i => Assert.Equal("4.5", i),
            i => Assert.Equal("5", i),
            i => Assert.Equal("6", i),
            i => Assert.Equal("7", i),
            i => Assert.Equal("8", i));
        Assert.Equal(3, b._start);
        Assert.Equal(3, b._end);

        b.Insert(7, "8.5");

        Assert.Collection(b,
            i => Assert.Equal("4", i),
            i => Assert.Equal("4.5", i),
            i => Assert.Equal("5", i),
            i => Assert.Equal("6", i),
            i => Assert.Equal("7", i),
            i => Assert.Equal("8", i),
            i => Assert.Equal("8.5", i),
            i => Assert.Equal("9", i),
            i => Assert.Equal("10", i),
            i => Assert.Equal("11", i));

        Assert.Collection(b._buffer,
            i => Assert.Equal("8.5", i),
            i => Assert.Equal("9", i),
            i => Assert.Equal("10", i),
            i => Assert.Equal("11", i),
            i => Assert.Equal("4", i),
            i => Assert.Equal("4.5", i),
            i => Assert.Equal("5", i),
            i => Assert.Equal("6", i),
            i => Assert.Equal("7", i),
            i => Assert.Equal("8", i));
        Assert.Equal(4, b._start);
        Assert.Equal(4, b._end);

        b.Insert(5, "7.5");

        Assert.Collection(b,
            i => Assert.Equal("4.5", i),
            i => Assert.Equal("5", i),
            i => Assert.Equal("6", i),
            i => Assert.Equal("7", i),
            i => Assert.Equal("7.5", i),
            i => Assert.Equal("8", i),
            i => Assert.Equal("8.5", i),
            i => Assert.Equal("9", i),
            i => Assert.Equal("10", i),
            i => Assert.Equal("11", i));
    }

    [Fact]
    public void InsertAfterRemove()
    {
        var b = CreateBuffer(5);

        b.Add("0");
        b.Add("1");
        b.Add("2");
        b.Add("3");
        b.Add("4");
        b.Add("5");
        b.Add("6");

        b.RemoveAt(2);

        b.Insert(3, "5.5");

        Assert.Collection(b,
            i => Assert.Equal("2", i),
            i => Assert.Equal("3", i),
            i => Assert.Equal("5", i),
            i => Assert.Equal("5.5", i),
            i => Assert.Equal("6", i));

        b.Add("7");

        Assert.Collection(b,
            i => Assert.Equal("3", i),
            i => Assert.Equal("5", i),
            i => Assert.Equal("5.5", i),
            i => Assert.Equal("6", i),
            i => Assert.Equal("7", i));
    }

    [Fact]
    public void RemoveAtMiddleUnderCapacity()
    {
        var b = CreateBuffer(10);

        b.Add("0");
        b.Add("1");
        b.Add("2");
        b.Add("3");
        b.Add("4");
        b.Add("5");
        b.Add("6");

        b.RemoveAt(2);

        Assert.Collection(b,
            i => Assert.Equal("0", i),
            i => Assert.Equal("1", i),
            i => Assert.Equal("3", i),
            i => Assert.Equal("4", i),
            i => Assert.Equal("5", i),
            i => Assert.Equal("6", i));

        b.Add("7");
        b.RemoveAt(2);

        Assert.Collection(b,
            i => Assert.Equal("0", i),
            i => Assert.Equal("1", i),
            i => Assert.Equal("4", i),
            i => Assert.Equal("5", i),
            i => Assert.Equal("6", i),
            i => Assert.Equal("7", i));

        b.Add("8");
        b.RemoveAt(4);

        Assert.Collection(b,
            i => Assert.Equal("0", i),
            i => Assert.Equal("1", i),
            i => Assert.Equal("4", i),
            i => Assert.Equal("5", i),
            i => Assert.Equal("7", i),
            i => Assert.Equal("8", i));

        b.Add("9");
        b.RemoveAt(5);

        Assert.Collection(b,
            i => Assert.Equal("0", i),
            i => Assert.Equal("1", i),
            i => Assert.Equal("4", i),
            i => Assert.Equal("5", i),
            i => Assert.Equal("7", i),
            i => Assert.Equal("9", i));

        b.Add("10");
        b.RemoveAt(0);

        Assert.Collection(b,
            i => Assert.Equal("1", i),
            i => Assert.Equal("4", i),
            i => Assert.Equal("5", i),
            i => Assert.Equal("7", i),
            i => Assert.Equal("9", i),
            i => Assert.Equal("10", i));
    }

    [Fact]
    public void InsertInMiddleLarge()
    {
        var b = CreateBuffer(5);

        b.Add("0");
        b.Add("1");
        b.Add("2");
        b.Add("3");
        b.Add("4");
        b.Add("5");
        b.Add("6");
        b.Add("7");
        b.Add("8");
        b.Add("9");
        b.Add("10");
        b.Add("11");

        b.Insert(0, "6.5");
        Assert.Collection(b,
            i => Assert.Equal("7", i),
            i => Assert.Equal("8", i),
            i => Assert.Equal("9", i),
            i => Assert.Equal("10", i),
            i => Assert.Equal("11", i));

        b.Insert(1, "7.5");
        Assert.Collection(b,
            i => Assert.Equal("7.5", i),
            i => Assert.Equal("8", i),
            i => Assert.Equal("9", i),
            i => Assert.Equal("10", i),
            i => Assert.Equal("11", i));

        b.Insert(2, "8.5");
        Assert.Collection(b,
            i => Assert.Equal("8", i),
            i => Assert.Equal("8.5", i),
            i => Assert.Equal("9", i),
            i => Assert.Equal("10", i),
            i => Assert.Equal("11", i));

        b.Insert(3, "9.5");
        Assert.Collection(b,
            i => Assert.Equal("8.5", i),
            i => Assert.Equal("9", i),
            i => Assert.Equal("9.5", i),
            i => Assert.Equal("10", i),
            i => Assert.Equal("11", i));

        b.Insert(4, "10.5");
        Assert.Collection(b,
            i => Assert.Equal("9", i),
            i => Assert.Equal("9.5", i),
            i => Assert.Equal("10", i),
            i => Assert.Equal("10.5", i),
            i => Assert.Equal("11", i));

        b.Insert(5, "11.5");
        Assert.Collection(b,
            i => Assert.Equal("9.5", i),
            i => Assert.Equal("10", i),
            i => Assert.Equal("10.5", i),
            i => Assert.Equal("11", i),
            i => Assert.Equal("11.5", i));
    }

    [Fact]
    public void RemoveAtInMiddleLarge()
    {
        var b = CreateBuffer(5);

        b.Add("0");
        b.Add("1");
        b.Add("2");
        b.Add("3");
        b.Add("4");
        b.Add("5");
        b.Add("6");
        b.Add("7");
        b.Add("8");
        b.Add("9");
        b.Add("10");
        b.Add("11");

        b.RemoveAt(1);

        Assert.Collection(b,
            i => Assert.Equal("7", i),
            i => Assert.Equal("9", i),
            i => Assert.Equal("10", i),
            i => Assert.Equal("11", i));

        b.RemoveAt(1);

        Assert.Collection(b,
            i => Assert.Equal("7", i),
            i => Assert.Equal("10", i),
            i => Assert.Equal("11", i));

        b.RemoveAt(1);

        Assert.Collection(b,
            i => Assert.Equal("7", i),
            i => Assert.Equal("11", i));

        b.Add("12");
        b.Add("13");
        b.Add("14");

        Assert.Collection(b,
            i => Assert.Equal("7", i),
            i => Assert.Equal("11", i),
            i => Assert.Equal("12", i),
            i => Assert.Equal("13", i),
            i => Assert.Equal("14", i));

        b.Add("15");
        b.Add("16");

        Assert.Collection(b,
            i => Assert.Equal("12", i),
            i => Assert.Equal("13", i),
            i => Assert.Equal("14", i),
            i => Assert.Equal("15", i),
            i => Assert.Equal("16", i));
    }

    [Fact]
    public void RemoveAtAndInsertMiddleLarge()
    {
        var b = CreateBuffer(5);

        b.Add("0");
        b.Add("1");
        b.Add("2");
        b.Add("3");
        b.Add("4");
        b.Add("5");
        b.Add("6");
        b.Add("7");
        b.Add("8");
        b.Add("9");
        b.Add("10");
        b.Add("11");

        b.RemoveAt(1);

        Assert.Collection(b,
            i => Assert.Equal("7", i),
            i => Assert.Equal("9", i),
            i => Assert.Equal("10", i),
            i => Assert.Equal("11", i));

        b.RemoveAt(1);

        Assert.Collection(b,
            i => Assert.Equal("7", i),
            i => Assert.Equal("10", i),
            i => Assert.Equal("11", i));

        b.RemoveAt(1);

        Assert.Collection(b,
            i => Assert.Equal("7", i),
            i => Assert.Equal("11", i));

        b.Insert(0, "6");

        Assert.Collection(b,
            i => Assert.Equal("6", i),
            i => Assert.Equal("7", i),
            i => Assert.Equal("11", i));

        b.Insert(1, "6.5");

        Assert.Collection(b,
            i => Assert.Equal("6", i),
            i => Assert.Equal("6.5", i),
            i => Assert.Equal("7", i),
            i => Assert.Equal("11", i));

        b.Insert(3, "7.5");

        Assert.Collection(b,
            i => Assert.Equal("6", i),
            i => Assert.Equal("6.5", i),
            i => Assert.Equal("7", i),
            i => Assert.Equal("7.5", i),
            i => Assert.Equal("11", i));

        b.Insert(5, "12");

        Assert.Collection(b,
            i => Assert.Equal("6.5", i),
            i => Assert.Equal("7", i),
            i => Assert.Equal("7.5", i),
            i => Assert.Equal("11", i),
            i => Assert.Equal("12", i));
    }

    [Fact]
    public void RemoveAtStartToZero()
    {
        var b = CreateBuffer(5);

        b.Add("0");
        b.Add("1");
        b.Add("2");
        b.Add("3");
        b.Add("4");
        b.Add("5");
        b.Add("6");
        b.Add("7");
        b.Add("8");
        b.Add("9");
        b.Add("10");
        b.Add("11");

        b.RemoveAt(0);
        Assert.Collection(b,
            i => Assert.Equal("8", i),
            i => Assert.Equal("9", i),
            i => Assert.Equal("10", i),
            i => Assert.Equal("11", i));

        b.RemoveAt(0);
        Assert.Collection(b,
            i => Assert.Equal("9", i),
            i => Assert.Equal("10", i),
            i => Assert.Equal("11", i));

        b.RemoveAt(0);
        Assert.Collection(b,
            i => Assert.Equal("10", i),
            i => Assert.Equal("11", i));

        b.RemoveAt(0);
        Assert.Collection(b,
            i => Assert.Equal("11", i));

        b.RemoveAt(0);
        Assert.Empty(b);
    }

    [Fact]
    public void RemoveAtEndToZero()
    {
        var b = CreateBuffer(5);

        b.Add("0");
        b.Add("1");
        b.Add("2");
        b.Add("3");
        b.Add("4");
        b.Add("5");
        b.Add("6");
        b.Add("7");
        b.Add("8");
        b.Add("9");
        b.Add("10");
        b.Add("11");

        b.RemoveAt(4);
        Assert.Collection(b,
            i => Assert.Equal("7", i),
            i => Assert.Equal("8", i),
            i => Assert.Equal("9", i),
            i => Assert.Equal("10", i));

        b.RemoveAt(3);
        Assert.Collection(b,
            i => Assert.Equal("7", i),
            i => Assert.Equal("8", i),
            i => Assert.Equal("9", i));

        b.RemoveAt(2);
        Assert.Collection(b,
            i => Assert.Equal("7", i),
            i => Assert.Equal("8", i));

        b.RemoveAt(1);
        Assert.Collection(b,
            i => Assert.Equal("7", i));

        b.RemoveAt(0);
        Assert.Empty(b);
    }

    [Fact]
    public void Insert_BeforeEnd_EndInMiddle()
    {
        var values = new List<string>
        {
            "10",
            "12",
            "0",
            "2",
            "2",
            "4",
            "4",
            "6",
            "6",
            "8",
        };

        var buffer = new CircularBuffer<string>(values, capacity: 10, start: 2, end: 2);
        buffer.Insert(9, "11");

        Assert.Collection(buffer,
            i => Assert.Equal("2", i),
            i => Assert.Equal("2", i),
            i => Assert.Equal("4", i),
            i => Assert.Equal("4", i),
            i => Assert.Equal("6", i),
            i => Assert.Equal("6", i),
            i => Assert.Equal("8", i),
            i => Assert.Equal("10", i),
            i => Assert.Equal("11", i),
            i => Assert.Equal("12", i));
    }

    [Fact]
    public void Clear_EmptiesBuffer_ResetsIndex()
    {
        var b = CreateBuffer(5);

        b.Insert(0, "0");
        b.Insert(0, "1");
        b.Insert(0, "2");

        b.Clear();

        Assert.Empty(b);

        b.Insert(0, "0");
        b.Insert(0, "1");
        b.Insert(0, "2");

        Assert.Collection(b,
            i => Assert.Equal("2", i),
            i => Assert.Equal("1", i),
            i => Assert.Equal("0", i));
    }
}
