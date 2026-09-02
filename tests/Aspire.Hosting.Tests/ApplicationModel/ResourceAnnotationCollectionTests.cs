// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Tests.ApplicationModel;

public class ResourceAnnotationCollectionTests
{
    [Fact]
    public void Add_InsertsItem()
    {
        var collection = new ResourceAnnotationCollection();
        var annotation = new TestAnnotation("test");

        collection.Add(annotation);

        Assert.Single(collection);
        Assert.Same(annotation, collection[0]);
    }

    [Fact]
    public void Remove_RemovesItem()
    {
        var collection = new ResourceAnnotationCollection();
        var annotation = new TestAnnotation("test");
        collection.Add(annotation);

        var removed = collection.Remove(annotation);

        Assert.True(removed);
        Assert.Empty(collection);
    }

    [Fact]
    public void Clear_RemovesAllItems()
    {
        var collection = new ResourceAnnotationCollection();
        collection.Add(new TestAnnotation("one"));
        collection.Add(new TestAnnotation("two"));
        collection.Add(new TestAnnotation("three"));

        collection.Clear();

        Assert.Empty(collection);
    }

    [Fact]
    public void Indexer_SetsItem()
    {
        var collection = new ResourceAnnotationCollection();
        var original = new TestAnnotation("original");
        var replacement = new TestAnnotation("replacement");
        collection.Add(original);

        collection[0] = replacement;

        Assert.Single(collection);
        Assert.Same(replacement, collection[0]);
    }

    [Fact]
    public void OfType_ReturnsMatchingAnnotations()
    {
        var collection = new ResourceAnnotationCollection();
        var testAnnotation1 = new TestAnnotation("test1");
        var testAnnotation2 = new TestAnnotation("test2");
        var otherAnnotation = new OtherAnnotation();
        collection.Add(testAnnotation1);
        collection.Add(otherAnnotation);
        collection.Add(testAnnotation2);

        var testAnnotations = collection.OfType<TestAnnotation>().ToArray();

        Assert.Equal(2, testAnnotations.Length);
        Assert.Contains(testAnnotation1, testAnnotations);
        Assert.Contains(testAnnotation2, testAnnotations);
    }

    [Fact]
    public void Any_ReturnsTrueWhenMatchingAnnotationExists()
    {
        var collection = new ResourceAnnotationCollection();
        collection.Add(new TestAnnotation("test"));

        Assert.True(collection.Any());
        Assert.True(collection.OfType<TestAnnotation>().Any());
        Assert.False(collection.OfType<OtherAnnotation>().Any());
    }

    [Fact]
    public void ToArray_CreatesArrayCopy()
    {
        var collection = new ResourceAnnotationCollection();
        collection.Add(new TestAnnotation("one"));
        collection.Add(new TestAnnotation("two"));

        var array = collection.ToArray();

        Assert.Equal(2, array.Length);
        Assert.Equal(collection[0], array[0]);
        Assert.Equal(collection[1], array[1]);
    }

    [Fact]
    public void FirstOrDefault_ReturnsFirstMatchingAnnotation()
    {
        var collection = new ResourceAnnotationCollection();
        var first = new TestAnnotation("first");
        var second = new TestAnnotation("second");
        collection.Add(first);
        collection.Add(second);

        var result = collection.OfType<TestAnnotation>().FirstOrDefault();

        Assert.Same(first, result);
    }

    [Fact]
    public void Enumeration_ReturnsSnapshot_ModificationsDuringEnumerationDoNotThrow()
    {
        var collection = new ResourceAnnotationCollection();
        for (var i = 0; i < 100; i++)
        {
            collection.Add(new TestAnnotation($"item{i}"));
        }

        var enumeratedItems = new List<IResourceAnnotation>();

        // Start enumerating, then modify collection during enumeration.
        // This should NOT throw because we enumerate a snapshot.
        foreach (var item in collection)
        {
            enumeratedItems.Add(item);
            if (enumeratedItems.Count == 50)
            {
                // Modify collection during enumeration - should not throw
                collection.Add(new TestAnnotation("new-during-enumeration"));
            }
        }

        // The snapshot had 100 items when enumeration started
        Assert.Equal(100, enumeratedItems.Count);

        // The collection now has 101 items
        Assert.Equal(101, collection.Count);
    }

    [Fact]
    public void Enumeration_ViaLinq_ReturnsSnapshot_ModificationsDuringEnumerationDoNotThrow()
    {
        var collection = new ResourceAnnotationCollection();
        for (var i = 0; i < 100; i++)
        {
            collection.Add(new TestAnnotation($"item{i}"));
        }

        var count = 0;

        // Use LINQ's OfType which goes through IEnumerable<T> interface dispatch
        foreach (var item in collection.OfType<TestAnnotation>())
        {
            count++;
            if (count == 50)
            {
                collection.Add(new TestAnnotation("new-during-enumeration"));
            }
        }

        Assert.Equal(100, count);
        Assert.Equal(101, collection.Count);
    }

    [Fact]
    public async Task ConcurrentEnumeration_WhileAddingAndRemoving_DoesNotThrow()
    {
        var collection = new ResourceAnnotationCollection();

        // Pre-populate with some items
        for (var i = 0; i < 100; i++)
        {
            collection.Add(new TestAnnotation($"initial{i}"));
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var exceptions = new List<Exception>();
        var addRemoveIterations = 0;
        var enumerationIterations = 0;

        // Task that continuously adds and removes items
        var modifierTask = Task.Run(async () =>
        {
            var random = new Random(42);
            while (!cts.Token.IsCancellationRequested)
            {
                try
                {
                    // Add a new item
                    collection.Add(new TestAnnotation($"added{Interlocked.Increment(ref addRemoveIterations)}"));

                    // Remove a random item if collection has items
                    if (collection.Count > 10)
                    {
                        collection.RemoveAt(random.Next(Math.Min(10, collection.Count)));
                    }

                    await Task.Yield();
                }
                catch (Exception ex)
                {
                    lock (exceptions)
                    {
                        exceptions.Add(ex);
                    }
                }
            }
        });

        // Multiple tasks that continuously enumerate the collection
        var enumeratorTasks = Enumerable.Range(0, 4).Select(i => Task.Run(async () =>
        {
            while (!cts.Token.IsCancellationRequested)
            {
                try
                {
                    // Direct enumeration
                    foreach (var item in collection)
                    {
                        GC.KeepAlive(item);
                    }

                    // LINQ enumeration via OfType (tests interface dispatch)
                    GC.KeepAlive(collection.OfType<TestAnnotation>().Count());

                    // LINQ enumeration via Any
                    GC.KeepAlive(collection.Any());

                    Interlocked.Increment(ref enumerationIterations);
                    await Task.Yield();
                }
                catch (Exception ex)
                {
                    lock (exceptions)
                    {
                        exceptions.Add(ex);
                    }
                }
            }
        })).ToArray();

        // Run for a short duration to exercise concurrent access
        await Task.Delay(500);
        await cts.CancelAsync();

        await modifierTask;
        await Task.WhenAll(enumeratorTasks);

        // Verify no exceptions occurred
        Assert.Empty(exceptions);

        // Verify we actually did some work
        Assert.True(addRemoveIterations > 0, "Should have performed add/remove operations");
        Assert.True(enumerationIterations > 0, "Should have performed enumeration operations");
    }

    [Fact]
    public void NonGenericEnumerable_GetEnumerator_ReturnsSnapshot()
    {
        var collection = new ResourceAnnotationCollection();
        collection.Add(new TestAnnotation("test"));

        // Cast to non-generic IEnumerable to test that interface implementation
        var enumerable = (System.Collections.IEnumerable)collection;
        var enumerator = enumerable.GetEnumerator();

        // Modify during enumeration
        collection.Add(new TestAnnotation("new"));

        var count = 0;
        while (enumerator.MoveNext())
        {
            count++;
        }

        // Should have iterated snapshot with 1 item, not 2
        Assert.Equal(1, count);
        Assert.Equal(2, collection.Count);
    }

    [Fact]
    public void GenericEnumerable_GetEnumerator_ReturnsSnapshot()
    {
        var collection = new ResourceAnnotationCollection();
        collection.Add(new TestAnnotation("test"));

        // Cast to generic IEnumerable<T> to test that interface implementation
        var enumerable = (IEnumerable<IResourceAnnotation>)collection;
        var enumerator = enumerable.GetEnumerator();

        // Modify during enumeration
        collection.Add(new TestAnnotation("new"));

        var count = 0;
        while (enumerator.MoveNext())
        {
            count++;
        }

        // Should have iterated snapshot with 1 item, not 2
        Assert.Equal(1, count);
        Assert.Equal(2, collection.Count);
    }

    private sealed class TestAnnotation(string name) : IResourceAnnotation
    {
        public string Name { get; } = name;

        public override string ToString() => Name;
    }

    private sealed class OtherAnnotation : IResourceAnnotation
    {
    }
}
