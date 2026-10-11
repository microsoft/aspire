// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

extern alias NativeLegacy;

using System.Text.Json;
using NativeLegacy::NativeHosting;

namespace Aspire.Hosting.Native.Core.Tests;

public class NativeCompositionConcurrencyTests
{
    [Fact]
    public async Task ParallelAuthoringPreservesEveryResourceAndAnnotation()
    {
        await using var builder = new NativeBuilder("unused");
        builder.DefineAnnotation("test/example", [new AnnotationFieldOptions { Name = "value", Type = "string", Required = true }]);
        await Parallel.ForEachAsync(Enumerable.Range(0, 100), TestContext.Current.CancellationToken, (index, _) =>
        {
            builder.AddResource($"resource-{index}", "custom", new ResourceOptions())
                .WithEnvironment("VALUE", index.ToString(System.Globalization.CultureInfo.InvariantCulture))
                .WithAnnotation("test/example", """{"value":"test"}""");

            return ValueTask.CompletedTask;
        });
        using var graph = JsonDocument.Parse(builder.Publish());
        Assert.Equal(100, graph.RootElement.GetProperty("resources").GetArrayLength());
        builder.Build();
        Assert.Throws<InvalidOperationException>(() => builder.AddResource("late", "custom", new ResourceOptions()));
    }
}
