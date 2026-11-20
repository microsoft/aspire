// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Xunit;

namespace Aspire.Dashboard.Tests.Model;

public class ColorGeneratorTests
{
    [Fact]
    public void ResolveAll_TagPalette_AssignsDistinctColorsRegardlessOfEncounterOrder()
    {
        string[] tags = ["Storefront", "Frontend", "Development", "Checkout", "Backend", "API"];
        var first = new ColorGenerator(ColorGenerator.s_tagVariableNames);
        var second = new ColorGenerator(ColorGenerator.s_tagVariableNames);
        first.ResolveAll(tags);
        second.ResolveAll(tags.Reverse());

        var colors = tags.Select(first.GetColorVariableByKey).ToArray();
        Assert.Equal(tags.Length, colors.Distinct().Count());
        Assert.Equal(colors, tags.Select(tag => second.GetColorVariableByKey(tag.ToLowerInvariant())));
    }

    [Fact]
    public void ResolveAll_NewTagsDoNotReassignExistingColors()
    {
        var generator = new ColorGenerator(ColorGenerator.s_tagVariableNames);
        generator.ResolveAll(["Backend", "Frontend"]);
        var backendColor = generator.GetColorVariableByKey("Backend");
        var frontendColor = generator.GetColorVariableByKey("Frontend");

        generator.ResolveAll(["API", "backend", "Frontend", "Storefront"]);

        Assert.Equal(backendColor, generator.GetColorVariableByKey("Backend"));
        Assert.Equal(frontendColor, generator.GetColorVariableByKey("Frontend"));
        Assert.Equal(4, new[] { backendColor, frontendColor, generator.GetColorVariableByKey("API"), generator.GetColorVariableByKey("Storefront") }.Distinct().Count());
    }
}
