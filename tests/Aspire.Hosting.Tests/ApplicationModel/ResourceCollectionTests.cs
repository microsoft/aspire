// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.ObjectModel;
using Microsoft.AspNetCore.InternalTesting;

namespace Aspire.Hosting.Tests.ApplicationModel;

public class ResourceCollectionTests
{
    [Fact]
    public void TryGetByName_FindsExistingResource()
    {
        var collection = new ResourceCollection();
        var resource = new TestResource("myResource");
        collection.Add(resource);

        var found = collection.TryGetByName("myResource", out var result);

        Assert.True(found);
        Assert.Same(resource, result);
    }

    [Fact]
    public void TryGetByName_IsCaseInsensitive()
    {
        var collection = new ResourceCollection();
        var resource = new TestResource("myResource");
        collection.Add(resource);

        var found = collection.TryGetByName("MYRESOURCE", out var result);

        Assert.True(found);
        Assert.Same(resource, result);
    }

    [Fact]
    public void TryGetByName_ReturnsFalseWhenNotFound()
    {
        var collection = new ResourceCollection();
        collection.Add(new TestResource("other"));

        var found = collection.TryGetByName("missing", out var result);

        Assert.False(found);
        Assert.Null(result);
    }

    [Fact]
    public void TryGetByName_ReturnsFalseWhenEmpty()
    {
        var collection = new ResourceCollection();

        var found = collection.TryGetByName("anything", out var result);

        Assert.False(found);
        Assert.Null(result);
    }

    [Fact]
    public void TryGetByName_ReflectsAddedResources()
    {
        var collection = new ResourceCollection();

        Assert.False(collection.TryGetByName("res", out _));

        collection.Add(new TestResource("res"));

        Assert.True(collection.TryGetByName("res", out _));
    }

    [Fact]
    public void TryGetByName_ReflectsRemovedResources()
    {
        var collection = new ResourceCollection();
        var resource = new TestResource("res");
        collection.Add(resource);

        Assert.True(collection.TryGetByName("res", out _));

        collection.Remove(resource);

        Assert.False(collection.TryGetByName("res", out _));
    }

    [Fact]
    public void TryGetByName_ReflectsRemovedAtResources()
    {
        var collection = new ResourceCollection();
        collection.Add(new TestResource("res"));

        Assert.True(collection.TryGetByName("res", out _));

        collection.RemoveAt(0);

        Assert.False(collection.TryGetByName("res", out _));
    }

    [Fact]
    public void TryGetByName_ReflectsClear()
    {
        var collection = new ResourceCollection();
        collection.Add(new TestResource("res1"));
        collection.Add(new TestResource("res2"));

        Assert.True(collection.TryGetByName("res1", out _));

        collection.Clear();

        Assert.False(collection.TryGetByName("res1", out _));
        Assert.False(collection.TryGetByName("res2", out _));
    }

    [Fact]
    public void TryGetByName_ReflectsInsert()
    {
        var collection = new ResourceCollection();
        collection.Add(new TestResource("res1"));

        var inserted = new TestResource("res2");
        collection.Insert(0, inserted);

        Assert.True(collection.TryGetByName("res2", out var result));
        Assert.Same(inserted, result);
    }

    [Fact]
    public void TryGetByName_ReflectsIndexerSet()
    {
        var collection = new ResourceCollection();
        collection.Add(new TestResource("old"));

        var replacement = new TestResource("new");
        collection[0] = replacement;

        Assert.False(collection.TryGetByName("old", out _));
        Assert.True(collection.TryGetByName("new", out var result));
        Assert.Same(replacement, result);
    }

    [Fact]
    public void Add_ThrowsOnDuplicateName()
    {
        var collection = new ResourceCollection();
        collection.Add(new TestResource("res"));

        var ex = Assert.Throws<DistributedApplicationException>(() => collection.Add(new TestResource("res")));
        Assert.Contains("res", ex.Message);
        Assert.Contains(nameof(TestResource), ex.Message);
    }

    [Fact]
    public void Add_ThrowsOnDuplicateNameCaseInsensitive()
    {
        var collection = new ResourceCollection();
        collection.Add(new TestResource("res"));

        Assert.Throws<DistributedApplicationException>(() => collection.Add(new TestResource("RES")));
    }

    [Fact]
    public void Insert_ThrowsOnDuplicateName()
    {
        var collection = new ResourceCollection();
        collection.Add(new TestResource("res"));

        Assert.Throws<DistributedApplicationException>(() => collection.Insert(0, new TestResource("res")));
    }

    [Fact]
    public void IndexerSet_ThrowsOnDuplicateName()
    {
        var collection = new ResourceCollection();
        collection.Add(new TestResource("res1"));
        collection.Add(new TestResource("res2"));

        // Setting [0] to a resource whose name matches [1] should throw.
        Assert.Throws<DistributedApplicationException>(() => collection[0] = new TestResource("res2"));
    }

    [Fact]
    public void IndexerSet_AllowsReplacingSameSlot()
    {
        var collection = new ResourceCollection();
        collection.Add(new TestResource("res"));

        // Replacing [0] with a new resource with the same name should succeed.
        var replacement = new TestResource("res");
        collection[0] = replacement;

        Assert.True(collection.TryGetByName("res", out var result));
        Assert.Same(replacement, result);
    }

    [Fact]
    public void Constructor_ThrowsOnDuplicateName()
    {
        var resources = new IResource[]
        {
            new TestResource("res"),
            new TestResource("res")
        };

        Assert.Throws<DistributedApplicationException>(() => new ResourceCollection(resources));
    }

    [Fact]
    public void TryGetByName_WorksWithConstructorEnumerable()
    {
        var resources = new IResource[]
        {
            new TestResource("res1"),
            new TestResource("res2")
        };
        var collection = new ResourceCollection(resources);

        Assert.True(collection.TryGetByName("res1", out var r1));
        Assert.Same(resources[0], r1);

        Assert.True(collection.TryGetByName("res2", out var r2));
        Assert.Same(resources[1], r2);
    }

    [Fact]
    public void TryGetByName_DefaultInterfaceMethod_WorksOnInterface()
    {
        // Test the DIM on IResourceCollection by using a wrapper that doesn't override TryGetByName.
        // ResourceCollection overrides TryGetByName, so calling through an IResourceCollection reference
        // would still dispatch to the class implementation. This wrapper exercises the DIM fallback.
        IResourceCollection collection = new SimpleResourceCollection();
        collection.Add(new TestResource("test"));

        Assert.True(collection.TryGetByName("test", out var result));
        Assert.Equal("test", result.Name);

        Assert.True(collection.TryGetByName("TEST", out _));

        Assert.False(collection.TryGetByName("nonexistent", out _));
    }

    [Fact]
    public void Enumeration_IsNotInvalidatedByConcurrentMutation()
    {
        // Regression test for https://github.com/microsoft/aspire/issues/19266.
        // Pipeline steps run concurrently and share a single DistributedApplicationModel, so one
        // step can append a resource while another is enumerating. Interleave deterministically by
        // driving the enumerator by hand rather than relying on thread timing.
        var collection = new ResourceCollection();
        collection.Add(new TestResource("res1"));
        collection.Add(new TestResource("res2"));

        var observed = new List<string>();

        using var enumerator = collection.GetEnumerator();

        Assert.True(enumerator.MoveNext());
        observed.Add(enumerator.Current.Name);

        collection.Add(new TestResource("added-during-enumeration"));
        collection.Remove(collection.Single(resource => resource.Name == "res2"));

        while (enumerator.MoveNext())
        {
            observed.Add(enumerator.Current.Name);
        }

        // The enumerator keeps serving the snapshot taken when enumeration started.
        Assert.Equal(["res1", "res2"], observed);
    }

    [Fact]
    public void Enumeration_ReflectsMutationsOnSubsequentEnumeration()
    {
        var collection = new ResourceCollection();
        collection.Add(new TestResource("res1"));

        Assert.Equal(["res1"], collection.Select(resource => resource.Name));

        collection.Add(new TestResource("res2"));

        Assert.Equal(["res1", "res2"], collection.Select(resource => resource.Name));

        collection.RemoveAt(0);

        Assert.Equal(["res2"], collection.Select(resource => resource.Name));
    }

    [Fact]
    public async Task ConcurrentAddsAndEnumerations_DoNotCorruptCollection()
    {
        const int resourceCount = 500;

        var collection = new ResourceCollection();
        var startGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var writer = Task.Run(async () =>
        {
            await startGate.Task;

            for (var i = 0; i < resourceCount; i++)
            {
                collection.Add(new TestResource($"res{i}"));
            }
        });

        var reader = Task.Run(async () =>
        {
            await startGate.Task;

            // Enumerate continuously while the writer appends. Without snapshot-based reads this
            // throws "Collection was modified; enumeration operation may not execute."
            while (!writer.IsCompleted)
            {
                foreach (var resource in collection)
                {
                    Assert.StartsWith("res", resource.Name, StringComparison.Ordinal);
                }
            }
        });

        startGate.SetResult();
        await Task.WhenAll(writer, reader).DefaultTimeout();

        var enumerated = collection.ToList();

        Assert.Equal(resourceCount, collection.Count);
        Assert.Equal(resourceCount, enumerated.Count);
        Assert.True(collection.TryGetByName($"res{resourceCount - 1}", out _));
    }

    [Fact]
    public async Task ConcurrentAdds_KeepListAndNameIndexConsistent()
    {
        const int writerCount = 8;
        const int perWriterCount = 250;

        var collection = new ResourceCollection();
        var startGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var writers = Enumerable.Range(0, writerCount).Select(writerIndex => Task.Run(async () =>
        {
            await startGate.Task;

            for (var i = 0; i < perWriterCount; i++)
            {
                collection.Add(new TestResource($"res{writerIndex}-{i}"));
            }
        })).ToArray();

        startGate.SetResult();
        await Task.WhenAll(writers).DefaultTimeout();

        // A torn List<T> append loses entries; a torn Dictionary write loses name lookups.
        Assert.Equal(writerCount * perWriterCount, collection.Count);
        Assert.Equal(writerCount * perWriterCount, collection.Select(resource => resource.Name).Distinct().Count());

        foreach (var resource in collection)
        {
            Assert.True(collection.TryGetByName(resource.Name, out var found));
            Assert.Same(resource, found);
        }
    }

    private sealed class TestResource(string name) : Resource(name)
    {
    }

    /// <summary>
    /// A minimal IResourceCollection that does NOT override TryGetByName,
    /// so the default interface method (DIM) enumerator-based fallback is exercised.
    /// </summary>
    private sealed class SimpleResourceCollection : Collection<IResource>, IResourceCollection
    {
    }
}
