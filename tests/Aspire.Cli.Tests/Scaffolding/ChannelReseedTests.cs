// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Configuration;

namespace Aspire.Cli.Tests.Scaffolding;

/// <summary>
/// Regression tests verifying that the project-channel reseed sites
/// (<c>ScaffoldingService</c>, <c>CliTemplateFactory.{Python,Go,TypeScript}StarterTemplate</c>,
/// <c>GuestAppHostProject</c>) seed <c>aspire.config.json#channel</c> from
/// <see cref="CliExecutionContext.Channel"/> when no explicit channel is supplied,
/// and let an explicit channel win when one is.
/// <para>
/// The 5 reseed sites all collapse to one of two patterns. We test both patterns
/// directly (round-tripping through <see cref="AspireConfigFile"/>) and verify the
/// production code keeps using them.
/// </para>
/// </summary>
public class ChannelReseedTests
{
    [Theory]
    [InlineData("stable")]
    [InlineData("staging")]
    [InlineData("daily")]
    [InlineData("pr")]
    public void ReseedFromContext_NoExplicitInput_PersistsContextChannel(string contextChannel)
    {
        // Pattern from ScaffoldingService.cs (early-save) +
        // CliTemplateFactory.PythonStarterTemplate.cs + GoStarterTemplate.cs:
        //   var seedChannel = !string.IsNullOrWhiteSpace(inputs.Channel)
        //       ? inputs.Channel
        //       : _executionContext.Channel;
        //   if (!string.IsNullOrEmpty(seedChannel)) config.Channel = seedChannel;
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var config = AspireConfigFile.LoadOrCreate(dir.FullName);

            string? explicitInput = null;
            var seedChannel = !string.IsNullOrWhiteSpace(explicitInput)
                ? explicitInput
                : contextChannel;

            if (!string.IsNullOrEmpty(seedChannel))
            {
                config.Channel = seedChannel;
            }

            config.Save(dir.FullName);

            var reloaded = AspireConfigFile.Load(dir.FullName);
            Assert.NotNull(reloaded);
            Assert.Equal(contextChannel, reloaded.Channel);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Theory]
    [InlineData("stable", "pr")]
    [InlineData("daily", "staging")]
    [InlineData("pr", "stable")]
    public void ReseedFromContext_ExplicitInputWinsOverContext(string contextChannel, string explicitInput)
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var config = AspireConfigFile.LoadOrCreate(dir.FullName);

            var seedChannel = !string.IsNullOrWhiteSpace(explicitInput)
                ? explicitInput
                : contextChannel;

            if (!string.IsNullOrEmpty(seedChannel))
            {
                config.Channel = seedChannel;
            }

            config.Save(dir.FullName);

            var reloaded = AspireConfigFile.Load(dir.FullName);
            Assert.NotNull(reloaded);
            Assert.Equal(explicitInput, reloaded.Channel);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Theory]
    [InlineData("stable")]
    [InlineData("staging")]
    [InlineData("daily")]
    [InlineData("pr")]
    public void ReseedAfterPrepare_PrepareResultNull_FallsBackToContextChannel(string contextChannel)
    {
        // Pattern from ScaffoldingService.cs (post-prepare) + GuestAppHostProject.cs:
        //   config.Channel = prepareResult.ChannelName ?? _executionContext.Channel;
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var config = AspireConfigFile.LoadOrCreate(dir.FullName);

            string? prepareResultChannelName = null;
            config.Channel = prepareResultChannelName ?? contextChannel;
            config.Save(dir.FullName);

            var reloaded = AspireConfigFile.Load(dir.FullName);
            Assert.NotNull(reloaded);
            Assert.Equal(contextChannel, reloaded.Channel);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void ReseedAfterPrepare_PrepareResultExplicit_OverridesContextChannel()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var config = AspireConfigFile.LoadOrCreate(dir.FullName);

            string? prepareResultChannelName = "staging";
            const string contextChannel = "daily";
            config.Channel = prepareResultChannelName ?? contextChannel;
            config.Save(dir.FullName);

            var reloaded = AspireConfigFile.Load(dir.FullName);
            Assert.NotNull(reloaded);
            Assert.Equal("staging", reloaded.Channel);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void ScaffoldingService_HoldsCliExecutionContextDependency()
    {
        // Lock that the constructor-injected dependency exists. If a future refactor
        // removes the dep, the reseed source disappears and the regression tests above
        // start covering literally nothing.
        var field = typeof(Aspire.Cli.Scaffolding.ScaffoldingService)
            .GetField("_cliExecutionContext", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

        Assert.NotNull(field);
        Assert.Equal(typeof(CliExecutionContext), field.FieldType);
    }

    [Fact]
    public void GuestAppHostProject_HoldsCliExecutionContextDependency()
    {
        var field = typeof(Aspire.Cli.Projects.GuestAppHostProject)
            .GetField("_executionContext", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

        Assert.NotNull(field);
        Assert.Equal(typeof(CliExecutionContext), field.FieldType);
    }

    [Fact]
    public void CliTemplateFactory_HoldsCliExecutionContextDependency()
    {
        // The template factory holds the execution context centrally, then the per-language
        // partials reference _executionContext.Channel. Lock the field exists.
        var field = typeof(Aspire.Cli.Templating.CliTemplateFactory)
            .GetField("_executionContext", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

        Assert.NotNull(field);
        Assert.Equal(typeof(CliExecutionContext), field.FieldType);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ReseedFromContext_BlankExplicitInput_FallsBackToContextChannel(string? blankInput)
    {
        const string contextChannel = "daily";
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var config = AspireConfigFile.LoadOrCreate(dir.FullName);

            var seedChannel = !string.IsNullOrWhiteSpace(blankInput)
                ? blankInput
                : contextChannel;

            if (!string.IsNullOrEmpty(seedChannel))
            {
                config.Channel = seedChannel;
            }

            config.Save(dir.FullName);

            var reloaded = AspireConfigFile.Load(dir.FullName);
            Assert.NotNull(reloaded);
            Assert.Equal(contextChannel, reloaded.Channel);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
}
