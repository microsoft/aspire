// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using Aspire.Hosting.Utils;

namespace Aspire.Hosting.Tests;

public class AtsAnnotationTests
{
    private static readonly AnnotationDefinition<DenoStateDto> s_stateAnnotation = new("spike.deno/state");

    [Fact]
    public void WithSerializedAnnotation_RoundTripsValue()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var container = builder.AddContainer("container", "image")
            .WithSerializedAnnotation("my.id", "{\"value\":1}");

        Assert.Equal("{\"value\":1}", container.Resource.GetSerializedAnnotation("my.id"));
        Assert.Equal("{\"value\":1}", container.GetSerializedAnnotation("my.id"));
    }

    [Fact]
    public void WithSerializedAnnotation_ReplacesExistingValueWithSameId()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var container = builder.AddContainer("container", "image")
            .WithSerializedAnnotation("my.id", "first")
            .WithSerializedAnnotation("my.id", "second");

        Assert.Single(container.Resource.Annotations.OfType<AtsAnnotation>());
        Assert.Equal("second", container.Resource.GetSerializedAnnotation("my.id"));
    }

    [Fact]
    public void GetSerializedAnnotation_ThrowsWhenAbsent()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var container = builder.AddContainer("container", "image");

        Assert.Throws<InvalidOperationException>(() => container.Resource.GetSerializedAnnotation("missing"));
    }

    [Fact]
    public void HasSerializedAnnotation_ReflectsPresence()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var container = builder.AddContainer("container", "image");

        Assert.False(container.HasSerializedAnnotation("my.id"));
        Assert.False(container.Resource.HasSerializedAnnotation("my.id"));

        container.WithSerializedAnnotation("my.id", "value");

        Assert.True(container.HasSerializedAnnotation("my.id"));
        Assert.True(container.Resource.HasSerializedAnnotation("my.id"));
    }

    [Fact]
    public void WithAnnotation_TypedRoundTrip()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var state = new DenoStateDto { ScriptPath = "main.ts", Permissions = ["--allow-net"], Mode = DenoMode.Watch };

        var container = builder.AddContainer("container", "image")
            .WithAnnotation(s_stateAnnotation, state);

        var roundTripped = container.Resource.GetAnnotation(s_stateAnnotation);

        Assert.Equal("main.ts", roundTripped.ScriptPath);
        Assert.Equal(["--allow-net"], roundTripped.Permissions);
        Assert.Equal(DenoMode.Watch, roundTripped.Mode);
    }

    [Fact]
    public void WithAnnotation_SerializesUsingCrossLanguageJsonShape()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var state = new DenoStateDto { ScriptPath = "main.ts", Permissions = ["--allow-net"], Mode = DenoMode.Watch };

        var container = builder.AddContainer("container", "image")
            .WithAnnotation(s_stateAnnotation, state);

        // The stored JSON is what a TypeScript integration sees via JSON.parse, so it must use
        // camelCase property names and serialize enums as strings (matching JSON.stringify of the
        // generated TS interface). This guards the contract that a value written from C# is readable
        // from TypeScript and vice-versa.
        var json = container.Resource.GetSerializedAnnotation(s_stateAnnotation.Id);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        Assert.Equal("main.ts", root.GetProperty("scriptPath").GetString());
        Assert.Equal("--allow-net", root.GetProperty("permissions")[0].GetString());
        Assert.Equal("Watch", root.GetProperty("mode").GetString());
    }

    [Fact]
    public void TryGetAnnotation_ReturnsFalseWhenAbsentAndTrueWhenPresent()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var container = builder.AddContainer("container", "image");

        Assert.False(container.Resource.TryGetAnnotation(s_stateAnnotation, out var missing));
        Assert.Null(missing);

        container.WithAnnotation(s_stateAnnotation, new DenoStateDto { ScriptPath = "main.ts" });

        Assert.True(container.Resource.TryGetAnnotation(s_stateAnnotation, out var present));
        Assert.Equal("main.ts", present.ScriptPath);
    }

    private sealed class DenoStateDto
    {
        public string ScriptPath { get; set; } = "";
        public string[] Permissions { get; set; } = [];
        public DenoMode Mode { get; set; }
    }

    private enum DenoMode
    {
        Run,
        Watch
    }
}
