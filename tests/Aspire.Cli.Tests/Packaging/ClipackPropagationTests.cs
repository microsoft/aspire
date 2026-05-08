// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Xml.Linq;

namespace Aspire.Cli.Tests.Packaging;

/// <summary>
/// Asserts that the clipack staging projitems explicitly forwards AspireCliChannel
/// to the inner Aspire.Cli publish through the MSBuild task's AdditionalProperties.
/// The downstream baked metadata is also covered by the
/// <see cref="Aspire.Cli.Tests.AssemblyMetadataChannelTests"/> end-to-end check;
/// this test pins the projitems-level forwarding so a refactor that drops the
/// explicit forward fails here directly instead of surfacing as an obscure
/// channel mismatch in published bits.
/// </summary>
public class ClipackPropagationTests
{
    [Fact]
    public void CommonProjitems_ForwardsAspireCliChannel_ThroughAdditionalProperties()
    {
        var projitemsPath = Path.Combine(GetRepoRoot(), "eng", "clipack", "Common.projitems");
        Assert.True(File.Exists(projitemsPath), $"Expected projitems file at {projitemsPath}");

        var doc = XDocument.Load(projitemsPath);

        var forwardingItem = doc.Descendants()
            .Where(e => e.Name.LocalName == "AdditionalProperties")
            .FirstOrDefault(e =>
                string.Equals(
                    (string?)e.Attribute("Include"),
                    "AspireCliChannel=$(AspireCliChannel)",
                    StringComparison.Ordinal));

        Assert.NotNull(forwardingItem);

        // The forwarding must be guarded so an unset channel does not pass through
        // an empty AspireCliChannel= value to the inner publish.
        var condition = (string?)forwardingItem.Attribute("Condition");
        Assert.False(
            string.IsNullOrWhiteSpace(condition),
            "AspireCliChannel forwarding must have a Condition that skips empty values.");
        Assert.Contains("AspireCliChannel", condition!, StringComparison.Ordinal);
    }

    [Fact]
    public void CommonProjitems_PublishMSBuildTask_ConsumesAdditionalPropertiesItemGroup()
    {
        var projitemsPath = Path.Combine(GetRepoRoot(), "eng", "clipack", "Common.projitems");
        var doc = XDocument.Load(projitemsPath);

        var publishTask = doc.Descendants()
            .Where(e => e.Name.LocalName == "MSBuild")
            .FirstOrDefault(e =>
                string.Equals((string?)e.Attribute("Targets"), "Publish", StringComparison.Ordinal));

        Assert.NotNull(publishTask);

        var properties = (string?)publishTask.Attribute("Properties");
        Assert.False(string.IsNullOrWhiteSpace(properties));
        Assert.Contains("@(AdditionalProperties)", properties!, StringComparison.Ordinal);
    }

    private static string GetRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "global.json")))
        {
            dir = dir.Parent;
        }
        Assert.NotNull(dir);
        return dir.FullName;
    }
}
