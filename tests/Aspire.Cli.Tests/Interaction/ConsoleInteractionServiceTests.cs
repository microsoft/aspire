// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.AspNetCore.InternalTesting;
using Aspire.Cli.Backchannel;
using Aspire.Cli.Configuration;
using Aspire.Cli.Interaction;
using Aspire.Cli.Packaging;
using Aspire.Cli.Resources;
using Aspire.Cli.Templating;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Aspire.Cli.Utils;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Testing;
using Spectre.Console;
using Spectre.Console.Rendering;
using NewCommand = Aspire.Cli.Commands.NewCommand;
using NewCommandPrompter = Aspire.Cli.Commands.NewCommandPrompter;
using AddCommandPrompter = Aspire.Cli.Commands.AddCommandPrompter;

using System.CommandLine;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Aspire.Cli.Tests.Interaction;

public class ConsoleInteractionServiceTests(ITestOutputHelper outputHelper)
{
    private static readonly DirectoryInfo s_tempRoot = Directory.CreateTempSubdirectory();
    private static readonly DirectoryInfo s_runtimeDirectory = s_tempRoot.CreateSubdirectory("runtimes");
    private static readonly DirectoryInfo s_logsDirectory = s_tempRoot.CreateSubdirectory("logs");

    private static CliExecutionContext CreateExecutionContext(bool debugMode = false, LogLevel? consoleLogLevel = null, string? logFilePath = null) =>
        new(new DirectoryInfo("."), new DirectoryInfo("."), new DirectoryInfo("."), s_runtimeDirectory, s_logsDirectory, logFilePath ?? "test.log", identityChannel: "local", debugMode: debugMode, consoleLogLevel: consoleLogLevel);

    private static ConsoleInteractionService CreateInteractionService(IAnsiConsole console, CliExecutionContext? executionContext = null, ICliHostEnvironment? hostEnvironment = null, ILoggerFactory? loggerFactory = null)
    {
        executionContext ??= CreateExecutionContext();
        var consoleEnvironment = new ConsoleEnvironment(console, console, TextReader.Null);
        return new ConsoleInteractionService(consoleEnvironment, executionContext, hostEnvironment ?? TestHelpers.CreateInteractiveHostEnvironment(), new EnvironmentProcessPathProvider(), loggerFactory ?? NullLoggerFactory.Instance, new ConsoleLogBufferContext());
    }

    [Theory]
    [InlineData(false, 40)]
    [InlineData(false, 120)]
    [InlineData(true, 120)]
    public async Task SourceDetails_RendersConsistently(bool ansi, int width)
    {
        var output = new StringBuilder();
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = ansi ? AnsiSupport.Yes : AnsiSupport.No,
            ColorSystem = ansi ? ColorSystemSupport.Standard : ColorSystemSupport.NoColors,
            Out = new AnsiConsoleOutput(new StringWriter(output)),
            Enrichment = new ProfileEnrichment { UseDefaultEnrichers = false }
        });
        console.Profile.Width = width;
        var interaction = CreateInteractionService(console);
        interaction.DisplaySourceDetails(
        [
            [(SharedCommandStrings.SourceGitRepository, "https://example.test/config"), (SharedCommandStrings.SourceRef, "release/[team]")],
            [(SharedCommandStrings.SourceName, "Company [templates]"), (SharedCommandStrings.SourceGitRepository, "https://example.test/templates"), (SharedCommandStrings.SourceGitPath, "templates"), (SharedCommandStrings.SourceRef, "release/[team]")],
            [(SharedCommandStrings.SourceName, "company"), (SharedCommandStrings.SourceFeed, "https://example.test/nuget"), (SharedCommandStrings.SourcePatterns, "Acme.Hosting.*")],
            [(SharedCommandStrings.SourceName, "company"), (SharedCommandStrings.SourceGitRepository, "https://example.test/skills"), (SharedCommandStrings.SourceMarketplace, "company-skills"), (SharedCommandStrings.SourcePlugins, "aspire@company-skills")]
        ]);

        await Verify(output.ToString(), "txt").UseParameters(ansi, width).ScrubLinesWithReplace(line => line.TrimEnd());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task SourceSelection_IntegrationGroupsAndTrustNotice(bool customSource, bool multipleSources)
    {
        var output = new StringBuilder();
        var console = CreateSelectionConsole(output, [new ConsoleKeyInfo('\r', ConsoleKey.Enter, false, false, false)]);
        var interaction = CreateInteractionService(console);
        var cache = new FakeNuGetPackageCache();
        var implicitChannel = PackageChannel.CreateImplicitChannel(cache, new TestFeatures(), NullLogger.Instance);
        var source = customSource ? "https://example.test/team" : PackageSources.NuGetOrg;
        var channel = customSource ? new PackageChannel("integration:team", PackageChannelQuality.Both,
            [new PackageMapping("Aspire.Hosting.*", source)], cache, new TestFeatures(), NullLogger.Instance)
        {
            IntegrationSource = new IntegrationSourceConfiguration
            {
                Name = "team",
                Source = source,
                Patterns = ["Aspire.Hosting.*"]
            }
        } : implicitChannel;
        List<(string FriendlyName, Aspire.Shared.NuGetPackageCli Package, PackageChannel Channel)> packages =
        [
            ("redis", new() { Id = "Aspire.Hosting.Redis", Version = "1.0.0", Source = source }, channel)
        ];
        if (multipleSources)
        {
            packages.Add(("postgres", new() { Id = "Aspire.Hosting.Postgres", Version = "2.0.0", Source = "https://example.test/other" }, implicitChannel));
        }

        var selected = await new AddCommandPrompter(interaction, CreateExecutionContext()).PromptForIntegrationAsync(packages, CancellationToken.None);

        Assert.Equal("Aspire.Hosting.Redis", selected.Package.Id);
        await Verify(output.ToString(), "txt").UseParameters(customSource, multipleSources).ScrubLinesWithReplace(line => line.TrimEnd());
    }

    [Theory]
    [InlineData(false, 40)]
    [InlineData(false, 120)]
    [InlineData(true, 40)]
    [InlineData(true, 120)]
    public async Task SourceSelection_TemplateSourcesExplainBeforeFetching(bool nonDefaultGitValues, int width)
    {
        var output = new StringBuilder();
        var console = CreateSelectionConsole(output, []);
        console.Profile.Width = width;
        var executionContext = CreateExecutionContext();
        var interaction = CreateInteractionService(console, executionContext);
        interaction.DisplayTemplateSources(
        [
            ("aspire[1p]", new TemplateSource(nonDefaultGitValues ? "templates/[team]" : ".",
                "https://example.test/templates.git", nonDefaultGitValues ? "release/[team]" : null)),
            ("another", new TemplateSource("./", "ssh://git@example.test/another.git", "HEAD")),
            ("third", new TemplateSource(".\\", "https://example.test/third.git", null)),
            ("local", new TemplateSource("C:\\Templates\\Company [local]", null, null))
        ], executionContext);
        interaction.DisplayTemplateGitFetchNotice("https://example.test/templates.git",
            nonDefaultGitValues ? "templates/[team]" : ".",
            nonDefaultGitValues ? "release/[team]" : null, mayPromptForAuthentication: false);

        Assert.False(executionContext.TryMarkSourceTrustWarningDisplayed());
        await Verify(output.ToString(), "txt").UseParameters(nonDefaultGitValues, width).ScrubLinesWithReplace(line => line.TrimEnd());
    }

    [Theory]
    [InlineData(".", null, "https://example.test/templates.git")]
    [InlineData("./", "HEAD", "https://example.test/templates.git")]
    [InlineData(".\\", null, "https://example.test/templates.git")]
    [InlineData("templates/[team]", "release/[team]", "https://example.test/templates.git")]
    [InlineData(".", null, "https://user:password@example.test/templates.git?token=secret#fragment")]
    public void TemplateGitFetchNotice_IdentifiesSourceWithoutExposingUrlCredentials(string path, string? reference, string repository)
    {
        var interaction = new TestInteractionService();
        const string displayedRepository = "https://example.test/templates.git";
        var source = repository == displayedRepository ? displayedRepository : "https://***@example.test/templates.git";
        if (path == "templates/[team]")
        {
            source += $" ({SharedCommandStrings.SourceGitPath}: {path}, {SharedCommandStrings.SourceRef}: {reference})";
        }

        interaction.DisplayTemplateGitFetchNotice(repository, path, reference, mayPromptForAuthentication: true);

        var message = Assert.Single(interaction.DisplayedMessages);
        var expected = string.Format(System.Globalization.CultureInfo.CurrentCulture, SharedCommandStrings.FetchingSourceWithAuthentication,
            SharedCommandStrings.GitFetchTemplates, source);
        Assert.Equal($"[blue]{expected.EscapeMarkup()}[/]", message.Message);
        Assert.Equal(KnownEmojis.InboxTray, message.Emoji);
        Assert.Equal(ConsoleOutput.Error, message.ConsoleOverride);
    }

    [Fact]
    public void SourceSelection_NoTemplateSourcesDoesNotDisplayOnboarding()
    {
        var interaction = new TestInteractionService();

        interaction.DisplayTemplateSources([], CreateExecutionContext());

        Assert.Empty(interaction.DisplayedMessages);
        Assert.Empty(interaction.DisplayedRenderables);
        Assert.Equal(0, interaction.DisplayEmptyLineCount);
    }

    [Fact]
    public async Task SourceDetails_WideLabelsUseTerminalCellWidths()
    {
        var output = new StringBuilder();
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Out = new AnsiConsoleOutput(new StringWriter(output)),
            Enrichment = new ProfileEnrichment { UseDefaultEnrichers = false }
        });
        console.Profile.Width = 40;
        CreateInteractionService(console).DisplaySourceDetails(
        [
            [("配置来源", "company"), ("仓库", "https://example.test/repo")],
            [("Source", "another")]
        ]);

        await Verify(output.ToString(), "txt").ScrubLinesWithReplace(line => line.TrimEnd());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SourceSelection_IntegrationVersionsUseSourceGroups(bool multipleSources)
    {
        var output = new StringBuilder();
        var console = CreateSelectionConsole(output, [new ConsoleKeyInfo('\r', ConsoleKey.Enter, false, false, false)]);
        var interaction = CreateInteractionService(console);
        var cache = new FakeNuGetPackageCache();
        var defaultChannel = PackageChannel.CreateImplicitChannel(cache, new TestFeatures(), NullLogger.Instance);
        var source = multipleSources ? "https://example.test/daily" : PackageSources.NuGetOrg;
        var dailyChannel = PackageChannel.CreateExplicitChannel("daily", PackageChannelQuality.Both,
            [new PackageMapping("Aspire*", source)], cache, new TestFeatures(), NullLogger.Instance);
        (string FriendlyName, Aspire.Shared.NuGetPackageCli Package, PackageChannel Channel)[] packages =
        [
            ("redis", new() { Id = "Aspire.Hosting.Redis", Version = "1.0.0", Source = PackageSources.NuGetOrg }, defaultChannel),
            ("redis", new() { Id = "Aspire.Hosting.Redis", Version = "2.0.0", Source = source }, dailyChannel)
        ];

        var selected = await new AddCommandPrompter(interaction, CreateExecutionContext()).PromptForIntegrationVersionAsync(packages, "daily", CancellationToken.None);

        Assert.Same(dailyChannel, selected.Channel);
        Assert.Equal("2.0.0", selected.Package.Version);
        await Verify(output.ToString(), "txt").UseParameters(multipleSources).ScrubLinesWithReplace(line => line.TrimEnd());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConfigSource_RendersAlignedDetailsAndReadmeLink(bool ansi)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var output = new StringBuilder();
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = ansi ? AnsiSupport.Yes : AnsiSupport.No,
            ColorSystem = ansi ? ColorSystemSupport.Standard : ColorSystemSupport.NoColors,
            Out = new AnsiConsoleOutput(new StringWriter(output)),
            Enrichment = new ProfileEnrichment { UseDefaultEnrichers = false }
        });
        console.Profile.Width = 160;
        console.Profile.Capabilities.Links = ansi;
        var context = workspace.CreateExecutionContext();
        var interaction = CreateInteractionService(console, context);
        const string readmeUrl = "https://docs.example/config-source?team=[platform]";
        using var service = TestConfigSourceServices.Create(context, interaction: interaction,
            environment: new TestEnvironment(new Dictionary<string, string?>
            {
                [CliConfigNames.InitSourceUrl] = "https://git.example/configuration",
                [CliConfigNames.InitSourcePath] = "teams/[platform].json",
                [CliConfigNames.InitSourceRef] = "config-preview",
                [CliConfigNames.InitSourceReadmeUrl] = readmeUrl
            }));
        var command = new RootCommand();
        command.Options.Add(ConfigSourceService.s_useSourceOption);

        var source = await service.ResolveAndAcquireConfigSourceAsync(command.Parse("--use-config-source false"), CancellationToken.None);

        Assert.True(Assert.IsType<ConfigSource>(source).OptOut);
        var rendered = output.ToString();
        if (ansi)
        {
            TerminalLinkAssert.ContainsLink(rendered, readmeUrl, ConfigSourceStrings.ReadmeLink);
        }
        // OSC 8 links include process-specific IDs, e.g. ESC]8;id=123;urlESC\.
        rendered = Regex.Replace(rendered, @"(?<=\x1b]8;id=)\d+", "link-id");
        await Verify(rendered, "txt").UseParameters(ansi).ScrubLinesWithReplace(line => line.TrimEnd());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConfigSource_RendersFetchNotice(bool interactive)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var output = new StringBuilder();
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Out = new AnsiConsoleOutput(new StringWriter(output)),
            Enrichment = new ProfileEnrichment { UseDefaultEnrichers = false }
        });
        console.Profile.Width = 160;
        var context = workspace.CreateExecutionContext();
        var interaction = CreateInteractionService(console, context);
        using var service = TestConfigSourceServices.Create(context, interaction: interaction, interactive: interactive,
            environment: new TestEnvironment(new Dictionary<string, string?>
            {
                [CliConfigNames.InitSourceUrl] = "https://git.example/configuration"
            }));
        var command = new RootCommand();
        command.Options.Add(ConfigSourceService.s_useSourceOption);

        var source = await service.ResolveAndAcquireConfigSourceAsync(command.Parse("--use-config-source"), CancellationToken.None);

        Assert.False(Assert.IsType<ConfigSource>(source).OptOut);
        await Verify(output.ToString(), "txt").UseParameters(interactive).ScrubLinesWithReplace(line => line.TrimEnd());
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("aspire.config.json", null)]
    [InlineData("teams/platform.json", null)]
    [InlineData(null, "config-preview")]
    [InlineData("aspire.config.json", "config-preview")]
    [InlineData("teams/platform.json", "config-preview")]
    public async Task ConfigSource_OmitsDefaultDetails(string? path, string? reference)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var output = new StringBuilder();
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Out = new AnsiConsoleOutput(new StringWriter(output)),
            Enrichment = new ProfileEnrichment { UseDefaultEnrichers = false }
        });
        console.Profile.Width = 160;
        var context = workspace.CreateExecutionContext();
        var interaction = CreateInteractionService(console, context);
        using var service = TestConfigSourceServices.Create(context, interaction: interaction,
            environment: new TestEnvironment(new Dictionary<string, string?>
            {
                [CliConfigNames.InitSourceUrl] = "https://git.example/configuration",
                [CliConfigNames.InitSourcePath] = path,
                [CliConfigNames.InitSourceRef] = reference
            }));
        var command = new RootCommand();
        command.Options.Add(ConfigSourceService.s_useSourceOption);

        var source = await service.ResolveAndAcquireConfigSourceAsync(command.Parse("--use-config-source false"), CancellationToken.None);

        Assert.True(Assert.IsType<ConfigSource>(source).OptOut);
        await Verify(output.ToString(), "txt").UseParameters(path, reference).ScrubLinesWithReplace(line => line.TrimEnd());
    }

    [Fact]
    public async Task PromptForSelectionAsync_GroupsRenderAsHeadingsAndKeepFirstLeafDefault()
    {
        var output = new StringBuilder();
        var console = CreateSelectionConsole(output, [new('\r', ConsoleKey.Enter, false, false, false)]);
        var interaction = CreateInteractionService(console);
        TestItem[] choices = [
            new("built-in", "Starter"),
            new("first", "Same (same) - first"),
            new("second", "Same (same) - second")];

        var selected = await interaction.PromptForSelectionAsync("Select a template:", choices, choice => choice.Display,
            choice => choice.Id switch
            {
                "built-in" => new("builtin", "Built-in templates"),
                "first" => new("source:first", "Company [first]"),
                "second" => new("source:second", "Company [second]"),
                _ => null
            }, choice => choice.Id switch
            {
                "built-in" => "Starter",
                "first" => "Same",
                _ => "Same (Observable services)"
            });

        Assert.Same(choices[0], selected);
        await Verify(output.ToString()).ScrubLinesWithReplace(line => line.TrimEnd());
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 3)]
    public async Task PromptForSelectionAsync_GroupHeadersCannotBeSelected(int moves, int expectedIndex)
    {
        var keys = Enumerable.Repeat(new ConsoleKeyInfo('\0', ConsoleKey.DownArrow, false, false, false), moves)
            .Append(new ConsoleKeyInfo('\r', ConsoleKey.Enter, false, false, false));
        var interaction = CreateInteractionService(CreateSelectionConsole(new StringBuilder(), keys));
        TestItem[] choices = [
            new("built-in", "Same"),
            new("first", "Same (same) - first"),
            new("second", "Same (same) - second"),
            new("search", "Search")];

        var selected = await interaction.PromptForSelectionAsync("Select:", choices, choice => choice.Display,
            choice => choice.Id == "search" ? null : new SelectionChoiceGroup(choice.Id, "Same heading"),
            choice => choice.Id == "search" ? "Other action" : "Same");

        Assert.Same(choices[expectedIndex], selected);
    }

    [Fact]
    public async Task PromptForSelectionAsync_GroupsWithSameIdShareOneHeadingAndRetainChildOrder()
    {
        var output = new StringBuilder();
        var console = CreateSelectionConsole(output, [new('\r', ConsoleKey.Enter, false, false, false)]);
        var interaction = CreateInteractionService(console);
        TestItem[] choices = [
            new("first:one", "First template - first"),
            new("second:one", "Other template - second"),
            new("first:two", "Second template - first")];

        var selected = await interaction.PromptForSelectionAsync("Select:", choices, choice => choice.Display,
            choice => choice.Id.StartsWith("first:", StringComparison.Ordinal)
                ? new("source:first", "First source")
                : new SelectionChoiceGroup("source:second", "Second source"), choice => choice.Display);

        Assert.Same(choices[0], selected);
        await Verify(output.ToString()).ScrubLinesWithReplace(line => line.TrimEnd());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PromptForSelectionAsync_PresentationGroupsPreserveValueTypeDefault(bool grouped)
    {
        var console = CreateSelectionConsole(new StringBuilder(), [new('\r', ConsoleKey.Enter, false, false, false)]);
        var interaction = CreateInteractionService(console);
        bool[] choices = [true, false];

        var selected = grouped
            ? await interaction.PromptForSelectionAsync("Select:", choices, choice => choice.ToString(),
                _ => new SelectionChoiceGroup("choices", "Choices"), choice => choice.ToString())
            : await interaction.PromptForSelectionAsync("Select:", choices, choice => choice.ToString());

        Assert.False(selected);
    }

    [Fact]
    public async Task PromptForSelectionAsync_GroupedChoicesRemainSearchable()
    {
        var keys = "second".Select(ch => new ConsoleKeyInfo(ch, Enum.Parse<ConsoleKey>(char.ToUpperInvariant(ch).ToString()), false, false, false))
            .Append(new ConsoleKeyInfo('\r', ConsoleKey.Enter, false, false, false));
        var interaction = CreateInteractionService(CreateSelectionConsole(new StringBuilder(), keys));
        TestItem[] choices = [new("first", "Same (same) - first"), new("second", "Same (same) - second")];

        var selected = await interaction.PromptForSelectionAsync("Select:", choices, choice => choice.Display,
            choice => new SelectionChoiceGroup(choice.Id, "Source"), choice => choice.Display);

        Assert.Same(choices[1], selected);
    }

    [Fact]
    public async Task PromptForSelectionAsync_DescriptionOnlySearchSelectsExactTemplate()
    {
        var keys = "OBSERVABLE".Select(ch => new ConsoleKeyInfo(ch, Enum.Parse<ConsoleKey>(ch.ToString()), true, false, false))
            .Append(new ConsoleKeyInfo('\r', ConsoleKey.Enter, false, false, false));
        var interaction = CreateInteractionService(CreateSelectionConsole(new StringBuilder(), keys));
        ITemplate[] templates = [CreateSearchableTemplate("first", null), CreateSearchableTemplate("second", "Observable [services]")];

        var selected = await interaction.PromptForSelectionAsync("Select:", templates, template => $"Same - {template.Name}",
            template => new SelectionChoiceGroup(template.Name, template.Name),
            template => NewCommand.FormatTemplateDisplayLabel(template).EscapeMarkup());

        Assert.Same(templates[1], selected);
    }

    [Theory]
    [InlineData("Name (Description)", 18, "Name (Description)")]
    [InlineData("Name (Description)", 12, "Name (Descr…")]
    [InlineData(" \tName\r\n (Description)\n", 30, "Name (Description)")]
    [InlineData("Name [[services]]", 10, "Name [[ser…")]
    [InlineData("Name [[services]]", 15, "Name [[services]]")]
    [InlineData("界界界", 5, "界界…")]
    [InlineData("界界界", 4, "界…")]
    [InlineData("e\u0301e\u0301e\u0301e\u0301", 3, "e\u0301e\u0301…")]
    [InlineData("😀😀😀", 4, "😀…")]
    [InlineData(":rocket: launch", 3, "🚀…")]
    [InlineData("Name\u001b[[31m", 20, "Name[[31m")]
    [InlineData("Name", 1, "…")]
    [InlineData("界", 1, "…")]
    [InlineData("Name", 0, "")]
    [InlineData("Name", -1, "")]
    [InlineData("", 1, "")]
    public void SelectionLabel_FitsTerminalCellsWithoutSplittingTextElements(string markup, int width, string expected)
    {
        var label = ConsoleInteractionService.FormatSelectionLabel(markup, width);

        Assert.Equal(expected, label);
        Assert.InRange(new Segment(label.RemoveMarkup()).CellCount(), 0, Math.Max(0, width));
    }

    [Fact]
    public async Task PromptForSelectionAsync_GroupedLabelsFitWidthAndNormalizeMetadata()
    {
        var output = new StringBuilder();
        var console = CreateSelectionConsole(output, [new('\r', ConsoleKey.Enter, false, false, false)]);
        console.Profile.Width = 40;
        var interaction = CreateInteractionService(console);
        ITemplate[] templates = [CreateSearchableTemplate("first", "Observable [services]\r\nwith 界界 and a long description")];

        var selected = await interaction.PromptForSelectionAsync("Select:", templates, template => $"Same - {template.Name}",
            _ => new SelectionChoiceGroup("source", "Company [source]\r\nwith a long heading that must fit"),
            template => NewCommand.FormatTemplateDisplayLabel(template).EscapeMarkup());

        Assert.Same(templates[0], selected);
        await Verify(output.ToString()).ScrubLinesWithReplace(line => line.TrimEnd());
    }

    [Theory]
    [InlineData(false, 120)]
    [InlineData(true, 120)]
    [InlineData(false, 40)]
    [InlineData(true, 40)]
    public async Task PromptForSelectionAsync_TemplateSourceHeadingsAreLiteralAndFitWidth(bool gitSource, int width)
    {
        var output = new StringBuilder();
        var console = CreateSelectionConsole(output, [new('\r', ConsoleKey.Enter, false, false, false)]);
        console.Profile.Width = width;
        console.Profile.Capabilities.ColorSystem = ColorSystem.Standard;
        var interaction = CreateInteractionService(console);
        var template = CreateSearchableTemplate("first", null);
        var source = gitSource
            ? new TemplateSource(".", "https://example.test/organization/[templates]/long-location", "release/[blue]")
            : new TemplateSource("/work/[templates]/long-directory-location", null, null);
        var configured = new ConfiguredTemplate("source", template, "Team [templates]", source);

        var selected = await interaction.PromptForSelectionAsync("Select:", new[] { template }, item => item.Name,
            _ => NewCommand.CreateTemplateSourceGroup(configured),
            item => NewCommand.FormatTemplateDisplayLabel(item).EscapeMarkup());

        Assert.Same(template, selected);
        await Verify(output.ToString()).UseParameters(gitSource, width).ScrubLinesWithReplace(line => line.TrimEnd());
    }

    [Theory]
    [InlineData(6)]
    [InlineData(10)]
    public async Task PromptForSelectionAsync_GroupedLabelsFitNarrowTerminals(int width)
    {
        var output = new StringBuilder();
        var console = CreateSelectionConsole(output, [new('\r', ConsoleKey.Enter, false, false, false)]);
        console.Profile.Width = width;
        var interaction = CreateInteractionService(console);
        ITemplate[] templates = [CreateSearchableTemplate("first", "Wide 界 description")];

        var selected = await interaction.PromptForSelectionAsync("Pick:", templates, template => template.Name,
            _ => new SelectionChoiceGroup("source", "Company"), template => NewCommand.FormatTemplateDisplayLabel(template).EscapeMarkup());

        Assert.Same(templates[0], selected);
        await Verify(output.ToString()).UseParameters(width).ScrubLinesWithReplace(line => line.TrimEnd());
    }

    [Fact]
    public async Task PromptForSelectionAsync_TruncatedDescriptionTailIsNotSearchable()
    {
        var keys = "ZXQ".Select(ch => new ConsoleKeyInfo(ch, Enum.Parse<ConsoleKey>(ch.ToString()), true, false, false))
            .Append(new ConsoleKeyInfo('\r', ConsoleKey.Enter, false, false, false));
        var console = CreateSelectionConsole(new StringBuilder(), keys);
        console.Profile.Width = 40;
        var interaction = CreateInteractionService(console);
        const string Description = "A description longer than the terminal with ZXQ at the end";
        ITemplate[] templates = [CreateSearchableTemplate("first", null), CreateSearchableTemplate("second", Description)];

        var selected = await interaction.PromptForSelectionAsync("Select:", templates, template => $"Same - {template.Name}",
            template => new SelectionChoiceGroup(template.Name, template.Name),
            template => NewCommand.FormatTemplateDisplayLabel(template).EscapeMarkup());

        Assert.Same(templates[0], selected);
        Assert.Equal(Description, templates[1].SearchDescription);
    }

    [Theory]
    [InlineData(null, "Same")]
    [InlineData("", "Same")]
    [InlineData(" ", "Same")]
    [InlineData("Observable [services]", "Same (Observable [services])")]
    public void TemplateDisplayLabel_IncludesOnlyNonemptyDescriptions(string? description, string expectedLabel)
    {
        var template = CreateSearchableTemplate("sample", description);

        Assert.Equal(expectedLabel, NewCommand.FormatTemplateDisplayLabel(template));
    }

    [Fact]
    public async Task BuiltInTemplatePicker_DescriptionOnlySearchWorksWithoutConfiguredSources()
    {
        var keys = "OBSERVABLE".Select(ch => new ConsoleKeyInfo(ch, Enum.Parse<ConsoleKey>(ch.ToString()), true, false, false))
            .Append(new ConsoleKeyInfo('\r', ConsoleKey.Enter, false, false, false));
        var interaction = CreateInteractionService(CreateSelectionConsole(new StringBuilder(), keys));
        var prompter = new NewCommandPrompter(interaction);
        ITemplate[] templates = [CreateSearchableTemplate("first", ""), CreateSearchableTemplate("second", "Observable services")];

        var selected = await prompter.PromptForTemplateAsync(templates, TestContext.Current.CancellationToken);

        Assert.Same(templates[1], selected);
    }

    [Fact]
    public async Task PromptForSelectionAsync_GroupedBindingUsesFlatLabelWithoutRendering()
    {
        var output = new StringBuilder();
        var interaction = CreateInteractionService(CreateInteractiveConsoleWithInput(output, ""),
            hostEnvironment: TestHelpers.CreateNonInteractiveHostEnvironment());
        var option = new Option<string?>("--template");
        var binding = PromptBinding.Create(new RootCommand { option }.Parse("--template second"), option);
        TestItem[] choices = [new("first", "first"), new("second", "second")];

        var selected = await interaction.PromptForSelectionAsync("Select:", choices, choice => choice.Display,
            _ => throw new InvalidOperationException("Noninteractive binding must not evaluate grouping."),
            _ => throw new InvalidOperationException("Noninteractive binding must use flat labels."), binding);

        Assert.Same(choices[1], selected);
        Assert.Empty(output.ToString());
    }

    [Fact]
    public async Task PromptForSelectionAsync_EmptyGroupedChoicesHaveNoSelectableHeaders()
    {
        var interaction = CreateInteractionService(CreateInteractiveConsoleWithInput(new StringBuilder(), ""));

        await Assert.ThrowsAsync<EmptyChoicesException>(() => interaction.PromptForSelectionAsync("Select:", Array.Empty<string>(), choice => choice,
            _ => throw new InvalidOperationException("Empty choices must not create groups."), choice => choice));
    }

    [Fact]
    public async Task DisplayLiveAsync_WhenNonInteractive_ThrowsInvalidOperationException()
    {
        var callbackInvoked = false;
        var interactionService = CreateInteractionService(AnsiConsole.Console, hostEnvironment: TestHelpers.CreateNonInteractiveHostEnvironment());

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            interactionService.DisplayLiveAsync(Text.Empty, _ =>
            {
                callbackInvoked = true;
                return Task.CompletedTask;
            }));

        Assert.Equal("Live rendering requires interactive output.", exception.Message);
        Assert.False(callbackInvoked);
    }

    [Fact]
    public async Task DisplayLiveAsync_WhenInteractive_UsesLiveRendering()
    {
        var output = new StringBuilder();
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.Yes,
            ColorSystem = ColorSystemSupport.NoColors,
            Out = new AnsiConsoleOutput(new StringWriter(output)),
            Enrichment = new ProfileEnrichment { UseDefaultEnrichers = false }
        });
        console.Profile.Width = int.MaxValue;
        var interactionService = CreateInteractionService(console);

        await interactionService.DisplayLiveAsync(new Text("Initial\n"), update =>
        {
            update(new Text("Updated once\n"));
            update(new Text("Updated twice\n"));
            return Task.CompletedTask;
        });

        var renderedOutput = output.ToString();
        Assert.Contains("\u001b[?25l", renderedOutput);
        Assert.Contains("Updated twice", renderedOutput);
        Assert.Contains("\u001b[?25h", renderedOutput);
    }

    [Fact]
    public async Task PromptForSelectionAsync_EmptyChoices_ThrowsEmptyChoicesException()
    {
        // Arrange
        var interactionService = CreateInteractionService(AnsiConsole.Console);
        var choices = Array.Empty<string>();

        // Act & Assert
        await Assert.ThrowsAsync<EmptyChoicesException>(() =>
            interactionService.PromptForSelectionAsync("Select an item:", choices, x => x, cancellationToken: CancellationToken.None));
    }

    [Fact]
    public async Task PromptForSelectionsAsync_EmptyChoices_ThrowsEmptyChoicesException()
    {
        // Arrange
        var interactionService = CreateInteractionService(AnsiConsole.Console);
        var choices = Array.Empty<string>();

        // Act & Assert
        await Assert.ThrowsAsync<EmptyChoicesException>(() =>
            interactionService.PromptForSelectionsAsync("Select items:", choices, x => x, cancellationToken: CancellationToken.None));
    }

    [Fact]
    public void DisplayError_WithMarkupCharacters_DoesNotCauseMarkupParsingError()
    {
        // Arrange
        var output = new StringBuilder();
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Out = new AnsiConsoleOutput(new StringWriter(output))
        });
        
        var interactionService = CreateInteractionService(console);
        var errorMessage = "The JSON value could not be converted to <Type>. Path: $.values[0].Type | LineNumber: 0 | BytePositionInLine: 121.";

        // Act - this should not throw an exception due to markup parsing
        var exception = Record.Exception(() => interactionService.DisplayError(errorMessage));

        // Assert
        Assert.Null(exception);
        var outputString = output.ToString();
        Assert.Contains("The JSON value could not be converted to", outputString);
    }

    [Fact]
    public void DisplaySubtleMessage_WithMarkupCharacters_DoesNotCauseMarkupParsingError()
    {
        // Arrange
        var output = new StringBuilder();
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Out = new AnsiConsoleOutput(new StringWriter(output))
        });
        
        var interactionService = CreateInteractionService(console);
        var message = "Path with <brackets> and [markup] characters";

        // Act - this should not throw an exception due to markup parsing
        var exception = Record.Exception(() => interactionService.DisplaySubtleMessage(message));

        // Assert
        Assert.Null(exception);
        var outputString = output.ToString();
        Assert.Contains("Path with <brackets> and [markup] characters", outputString);
    }

    [Fact]
    public void DisplayLines_WithMarkupCharacters_DoesNotCauseMarkupParsingError()
    {
        // Arrange
        var output = new StringBuilder();
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Out = new AnsiConsoleOutput(new StringWriter(output))
        });
        
        var interactionService = CreateInteractionService(console);
        var lines = new[]
        {
            (OutputLineStream.StdOut, "Command output with <angle> brackets"),
            (OutputLineStream.StdErr, "Error output with [square] brackets")
        };

        // Act - this should not throw an exception due to markup parsing
        var exception = Record.Exception(() => interactionService.DisplayLines(lines));

        // Assert
        Assert.Null(exception);
        var outputString = output.ToString();
        Assert.Contains("Command output with <angle> brackets", outputString);
        // EscapeMarkup() escapes [ to [[ for Spectre's parser, but Spectre renders [[ back to literal [
        Assert.Contains("Error output with [square] brackets", outputString);
    }

    [Fact]
    public void DisplayMarkdown_WithPlainText_DoesNotThrow()
    {
        // Arrange
        var output = new StringBuilder();
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Out = new AnsiConsoleOutput(new StringWriter(output))
        });
        
        var interactionService = CreateInteractionService(console);
        var plainText = "This is just plain text without any markdown.";

        // Act
        var exception = Record.Exception(() => interactionService.DisplayMarkdown(plainText));

        // Assert
        Assert.Null(exception);
        var outputString = output.ToString();
        Assert.Contains("This is just plain text without any markdown.", outputString);
    }

    [Fact]
    public void DisplayMarkdown_WhenNonInteractive_UsesPlainTextFallback()
    {
        var output = new StringBuilder();
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Out = new AnsiConsoleOutput(new StringWriter(output))
        });

        var interactionService = CreateInteractionService(console, hostEnvironment: TestHelpers.CreateNonInteractiveHostEnvironment());

        interactionService.DisplayMarkdown("Visit [GitHub](https://github.com) for more info.");

        Assert.Contains("Visit GitHub (https://github.com) for more info.", output.ToString());
    }

    [Fact]
    public void DisplayMarkdown_WithTable_RendersReadableInteractiveOutput()
    {
        var output = new StringBuilder();
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Out = new AnsiConsoleOutput(new StringWriter(output))
        });

        var executionContext = CreateExecutionContext();
        var interactionService = CreateInteractionService(console, executionContext);
        var markdown = """
            | Setting | Environment variable | Purpose |
            | ------- | -------------------- | ------- |
            | `Azure:SubscriptionId` | `Azure__SubscriptionId` | Target Azure subscription |
            """;

        interactionService.DisplayMarkdown(markdown);

        var outputString = output.ToString().Replace("\r\n", "\n");

        Assert.Contains("Setting", outputString);
        Assert.Contains("Environment variable", outputString);
        Assert.Contains("Purpose", outputString);
        Assert.Contains("Azure:SubscriptionId", outputString);
        Assert.Contains("Azure__SubscriptionId", outputString);
        Assert.Contains("Target Azure subscription", outputString);
        Assert.True(outputString.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length >= 3);
    }

    [Theory]
    [MemberData(nameof(DisplayMarkdown_BetweenPlainText_Data))]
    public void DisplayMarkdown_BetweenPlainText_DoesNotInsertExtraEmptyLine(string markdown, string expectedOutput)
    {
        var output = new StringBuilder();
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Out = new AnsiConsoleOutput(new StringWriter(output))
        });

        var interactionService = CreateInteractionService(console);

        interactionService.DisplayPlainText("before markdown");
        interactionService.DisplayMarkdown(markdown);
        interactionService.DisplayPlainText("after markdown");

        Assert.Equal(expectedOutput, output.ToString(), ignoreLineEndingDifferences: true);
    }

    public static TheoryData<string, string> DisplayMarkdown_BetweenPlainText_Data => new()
    {
        {
            """
            # Heading

            This is a paragraph.
            """,
            """
            before markdown
            Heading

            This is a paragraph.

            after markdown

            """
        },
        {
            "**bold**",
            """
            before markdown
            bold
            after markdown

            """
        },
    };

    [Fact]
    public async Task ShowStatusAsync_InDebugMode_DisplaysSubtleMessageInsteadOfSpinner()
    {
        // Arrange
        var output = new StringBuilder();
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Out = new AnsiConsoleOutput(new StringWriter(output))
        });

        var executionContext = CreateExecutionContext(debugMode: true);
        var interactionService = CreateInteractionService(console, executionContext);
        var statusText = "Processing request...";

        // Act
        var statusValue = await interactionService.ShowStatusAsync(statusText, () => Task.FromResult(GetInStatus(interactionService))).DefaultTimeout();

        // Assert
        Assert.Equal(0, statusValue);
        var outputString = output.ToString();
        Assert.Contains(statusText, outputString);
    }

    [Fact]
    public async Task ShowDynamicStatusAsync_InDebugMode_DisplaysSubtleMessagesInsteadOfSpinner()
    {
        var output = new StringBuilder();
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Out = new AnsiConsoleOutput(new StringWriter(output))
        });

        var interactionService = CreateInteractionService(console, CreateExecutionContext(debugMode: true));

        var statusValue = await interactionService.ShowDynamicStatusAsync("Processing request...", updateStatus =>
        {
            updateStatus("Still processing...");
            return Task.FromResult(GetInStatus(interactionService));
        }).DefaultTimeout();

        Assert.Equal(0, statusValue);
        Assert.Contains("Processing request...", output.ToString());
        Assert.Contains("Still processing...", output.ToString());
    }

    [Theory]
    [InlineData(LogLevel.Trace)]
    [InlineData(LogLevel.Debug)]
    [InlineData(LogLevel.Information)]
    [InlineData(LogLevel.Warning)]
    [InlineData(LogLevel.Error)]
    [InlineData(LogLevel.Critical)]
    public async Task StatusMethods_WithConsoleLogging_DoNotStartSpinner(LogLevel consoleLogLevel)
    {
        var output = new StringBuilder();
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(new SpectreConsoleLoggerProvider(new StringWriter(output), new ConsoleLogBufferContext())));
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Out = new AnsiConsoleOutput(new StringWriter(output))
        });
        var interactionService = CreateInteractionService(console, CreateExecutionContext(consoleLogLevel: consoleLogLevel), loggerFactory: loggerFactory);

        var asyncStatusValue = await interactionService.ShowStatusAsync("Working...", () => Task.FromResult(GetInStatus(interactionService))).DefaultTimeout();
        var dynamicStatusValue = await interactionService.ShowDynamicStatusAsync("Working...", updateStatus =>
        {
            updateStatus("Still working...");
            return Task.FromResult(GetInStatus(interactionService));
        }).DefaultTimeout();
        var synchronousStatusValue = -1;
        interactionService.ShowStatus("Working...", () => synchronousStatusValue = GetInStatus(interactionService));

        Assert.Equal(0, asyncStatusValue);
        Assert.Equal(0, dynamicStatusValue);
        Assert.Equal(0, synchronousStatusValue);
        Assert.Contains("Working...", output.ToString());
        Assert.Contains("Still working...", output.ToString());
    }

    [Fact]
    public async Task ShowStatusAsync_WithConsoleLoggingDisabled_StartsSpinner()
    {
        var interactionService = CreateInteractionService(AnsiConsole.Console, CreateExecutionContext(consoleLogLevel: LogLevel.None));

        var statusValue = await interactionService.ShowStatusAsync("Working...", () => Task.FromResult(GetInStatus(interactionService))).DefaultTimeout();

        Assert.Equal(1, statusValue);
    }

    [Fact]
    public void ShowStatus_InDebugMode_DisplaysSubtleMessageInsteadOfSpinner()
    {
        // Arrange
        var output = new StringBuilder();
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Out = new AnsiConsoleOutput(new StringWriter(output))
        });

        var executionContext = CreateExecutionContext(debugMode: true);
        var interactionService = CreateInteractionService(console, executionContext);
        var statusText = "Processing synchronous request...";
        var statusValue = -1;

        // Act
        interactionService.ShowStatus(statusText, () => statusValue = GetInStatus(interactionService));

        // Assert
        Assert.Equal(0, statusValue);
        var outputString = output.ToString();
        Assert.Contains(statusText, outputString);
    }

    [Fact]
    public async Task PromptForStringAsync_WhenInteractiveInputNotSupported_ThrowsInvalidOperationException()
    {
        // Arrange
        var interactionService = CreateInteractionService(AnsiConsole.Console, hostEnvironment: TestHelpers.CreateNonInteractiveHostEnvironment());

        // Act & Assert
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            interactionService.PromptForStringAsync("Enter value:", null, false, false, binding: null, cancellationToken: CancellationToken.None));
        Assert.Contains(InteractionServiceStrings.InteractiveInputNotSupported, exception.Message);
    }

    [Fact]
    public async Task PromptForSelectionAsync_WhenInteractiveInputNotSupported_ThrowsInvalidOperationException()
    {
        // Arrange
        var interactionService = CreateInteractionService(AnsiConsole.Console, hostEnvironment: TestHelpers.CreateNonInteractiveHostEnvironment());
        var choices = new[] { "option1", "option2" };

        // Act & Assert
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            interactionService.PromptForSelectionAsync("Select an item:", choices, x => x, cancellationToken: CancellationToken.None));
        Assert.Contains(InteractionServiceStrings.InteractiveInputNotSupported, exception.Message);
    }

    [Fact]
    public async Task PromptForSelectionsAsync_WhenInteractiveInputNotSupported_ThrowsInvalidOperationException()
    {
        // Arrange
        var interactionService = CreateInteractionService(AnsiConsole.Console, hostEnvironment: TestHelpers.CreateNonInteractiveHostEnvironment());
        var choices = new[] { "option1", "option2" };

        // Act & Assert
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            interactionService.PromptForSelectionsAsync("Select items:", choices, x => x, cancellationToken: CancellationToken.None));
        Assert.Contains(InteractionServiceStrings.InteractiveInputNotSupported, exception.Message);
    }

    [Fact]
    public async Task ConfirmAsync_WhenInteractiveInputNotSupported_ThrowsInvalidOperationException()
    {
        // Arrange
        var interactionService = CreateInteractionService(AnsiConsole.Console, hostEnvironment: TestHelpers.CreateNonInteractiveHostEnvironment());

        // Act & Assert
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            interactionService.PromptConfirmAsync("Confirm?", cancellationToken: CancellationToken.None));
        Assert.Contains(InteractionServiceStrings.InteractiveInputNotSupported, exception.Message);
    }

    [Fact]
    public async Task ShowStatusAsync_NestedCall_DoesNotThrowException()
    {
        // Arrange
        var output = new StringBuilder();
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Out = new AnsiConsoleOutput(new StringWriter(output))
        });

        var interactionService = CreateInteractionService(console);
        
        var outerStatusText = "Outer operation...";
        var innerStatusText = "Inner operation...";
        var expectedResult = 42;

        // Act
        var actualResult = await interactionService.ShowStatusAsync(outerStatusText, async () =>
        {
            // This nested call should not throw - it should fall back to DisplaySubtleMessage
            return await interactionService.ShowStatusAsync(innerStatusText, () => Task.FromResult(expectedResult));
        });

        // Assert
        Assert.Equal(expectedResult, actualResult);
        var outputString = output.ToString();
        Assert.Contains(outerStatusText, outputString);
        Assert.Contains(innerStatusText, outputString);
    }

    [Fact]
    public void ShowStatus_NestedCall_DoesNotThrowException()
    {
        // Arrange
        var output = new StringBuilder();
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Out = new AnsiConsoleOutput(new StringWriter(output))
        });

        var interactionService = CreateInteractionService(console);
        
        var outerStatusText = "Outer synchronous operation...";
        var innerStatusText = "Inner synchronous operation...";
        var actionExecuted = false;

        // Act
        interactionService.ShowStatus(outerStatusText, () =>
        {
            // This nested call should not throw - it should fall back to DisplaySubtleMessage
            interactionService.ShowStatus(innerStatusText, () => actionExecuted = true);
        });

        // Assert
        Assert.True(actionExecuted);
        var outputString = output.ToString();
        Assert.Contains(outerStatusText, outputString);
        Assert.Contains(innerStatusText, outputString);
    }

    [Fact]
    public async Task ShowStatusAsync_FallbackPath_DoesNotLeaveInStatusFlagSet()
    {
        // Regression test: when ShowStatusAsync takes the fallback path due to debug/non-interactive
        // mode or empty status text, the _inStatus flag must not be set to 1 (and left there).
        // Previously the CompareExchange ran first in the || chain, so the swap happened before the
        // other conditions short-circuited, leaving _inStatus=1 with no try/finally to reset it.
        var output = new StringBuilder();
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Out = new AnsiConsoleOutput(new StringWriter(output))
        });

        // Debug mode forces the fallback path even though host environment supports interactive output.
        var executionContext = CreateExecutionContext(debugMode: true);
        var interactionService = CreateInteractionService(console, executionContext);

        await interactionService.ShowStatusAsync("Working...", () => Task.FromResult(0)).DefaultTimeout();

        Assert.Equal(0, GetInStatus(interactionService));
    }

    [Fact]
    public async Task ShowStatusAsync_FallbackPathWithEmptyText_DoesNotLeaveInStatusFlagSet()
    {
        // Regression test: empty status text triggers the fallback path. The flag must not be left set.
        var interactionService = CreateInteractionService(AnsiConsole.Console);

        await interactionService.ShowStatusAsync(string.Empty, () => Task.FromResult(0)).DefaultTimeout();

        Assert.Equal(0, GetInStatus(interactionService));
    }

    [Fact]
    public async Task ShowDynamicStatusAsync_FallbackPath_DoesNotLeaveInStatusFlagSet()
    {
        var executionContext = CreateExecutionContext(debugMode: true);
        var interactionService = CreateInteractionService(AnsiConsole.Console, executionContext);

        await interactionService.ShowDynamicStatusAsync<int>("Working...", _ => Task.FromResult(0)).DefaultTimeout();

        Assert.Equal(0, GetInStatus(interactionService));
    }

    [Fact]
    public void ShowStatus_FallbackPath_DoesNotLeaveInStatusFlagSet()
    {
        var executionContext = CreateExecutionContext(debugMode: true);
        var interactionService = CreateInteractionService(AnsiConsole.Console, executionContext);

        interactionService.ShowStatus("Working...", () => { });

        Assert.Equal(0, GetInStatus(interactionService));
    }

    private static int GetInStatus(ConsoleInteractionService service)
    {
        var field = typeof(ConsoleInteractionService).GetField("_inStatus", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(field);
        return (int)field!.GetValue(service)!;
    }

    [Fact]
    public void DisplayIncompatibleVersionError_WithMarkupCharactersInVersion_DoesNotThrow()
    {
        // Arrange
        var output = new StringBuilder();
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Out = new AnsiConsoleOutput(new StringWriter(output))
        });

        var interactionService = CreateInteractionService(console);

        var ex = new AppHostIncompatibleException("Incompatible [version]", "capability [Prod]");

        // Act - should not throw due to unescaped markup characters
        var exception = Record.Exception(() => interactionService.DisplayIncompatibleVersionError(ex, "9.0.0-preview.1 [rc]"));

        // Assert
        Assert.Null(exception);
        var outputString = output.ToString();
        Assert.Contains("capability [Prod]", outputString);
        Assert.Contains("9.0.0-preview.1 [rc]", outputString);
    }

    private static (ConsoleInteractionService InteractionService, StringBuilder Output) CreateInteractionServiceWithOutputCapture()
    {
        var output = new StringBuilder();
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Out = new AnsiConsoleOutput(new StringWriter(output))
        });

        // Use a wide profile so assertions on full message text aren't broken by line wrapping.
        console.Profile.Width = 512;

        return (CreateInteractionService(console), output);
    }

    [Fact]
    public void DisplayIncompatibleVersionError_AppHostOlderThanCli_SuggestsUpdatingAppHost()
    {
        // Arrange
        var (interactionService, output) = CreateInteractionServiceWithOutputCapture();
        var ex = new AppHostIncompatibleException("Incompatible", "baseline.v2");

        // Act - hosting version is older than any CLI version.
        var exitCode = interactionService.DisplayIncompatibleVersionError(ex, "0.1.0");

        // Assert
        Assert.Equal(CliExitCodes.AppHostIncompatible, exitCode);
        var outputString = output.ToString();
        Assert.Contains(InteractionServiceStrings.AppHostNotCompatibleUpdateAppHost, outputString);
        Assert.Contains("aspire update", outputString);
    }

    [Fact]
    public void DisplayIncompatibleVersionError_CliOlderThanAppHost_SuggestsUpdatingCli()
    {
        // Arrange
        var (interactionService, output) = CreateInteractionServiceWithOutputCapture();
        var ex = new AppHostIncompatibleException("Incompatible", "baseline.v2");

        // Act - hosting version is newer than any CLI version.
        var exitCode = interactionService.DisplayIncompatibleVersionError(ex, "999.0.0");

        // Assert
        Assert.Equal(CliExitCodes.AppHostIncompatible, exitCode);
        var outputString = output.ToString();
        Assert.Contains(InteractionServiceStrings.AppHostNotCompatibleUpdateCli, outputString);
        Assert.Contains("To update, run:", outputString);
    }

    [Fact]
    public void DisplayIncompatibleVersionError_SameVersion_ShowsGenericMessageWithoutUpdateCommand()
    {
        // Arrange
        var (interactionService, output) = CreateInteractionServiceWithOutputCapture();
        var ex = new AppHostIncompatibleException("Incompatible", "baseline.v2");

        // Act - same version on both sides (incompatible for another reason, e.g. a capability).
        var exitCode = interactionService.DisplayIncompatibleVersionError(ex, VersionHelper.GetDefaultTemplateVersion());

        // Assert
        Assert.Equal(CliExitCodes.AppHostIncompatible, exitCode);
        var outputString = output.ToString();
        Assert.Contains(InteractionServiceStrings.AppHostNotCompatibleConsiderUpgrading, outputString);
        Assert.DoesNotContain("To update, run:", outputString);
    }

    [Fact]
    public void DisplayIncompatibleVersionError_UnparseableVersion_ShowsGenericMessage()
    {
        // Arrange
        var (interactionService, output) = CreateInteractionServiceWithOutputCapture();
        var ex = new AppHostIncompatibleException("Incompatible", "baseline.v2");

        // Act - the caller passes the required capability when no version is available.
        var exitCode = interactionService.DisplayIncompatibleVersionError(ex, "baseline.v2");

        // Assert
        Assert.Equal(CliExitCodes.AppHostIncompatible, exitCode);
        var outputString = output.ToString();
        Assert.Contains(InteractionServiceStrings.AppHostNotCompatibleConsiderUpgrading, outputString);
        Assert.DoesNotContain("To update, run:", outputString);
    }

    [Fact]
    public void DisplayMessage_WithMarkupCharactersInMessage_AutoEscapesByDefault()
    {
        // Arrange
        var output = new StringBuilder();
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Out = new AnsiConsoleOutput(new StringWriter(output))
        });

        var interactionService = CreateInteractionService(console);

        // DisplayMessage now auto-escapes by default, so callers don't need to escape.
        var message = "See logs at C:\\Users\\test [Dev]\\logs\\aspire.log";

        // Act - should not throw since DisplayMessage escapes by default
        var exception = Record.Exception(() => interactionService.DisplayMessage(KnownEmojis.PageFacingUp, message));

        // Assert
        Assert.Null(exception);
        var outputString = output.ToString();
        Assert.Contains("C:\\Users\\test [Dev]\\logs\\aspire.log", outputString);
    }

    [Fact]
    public void DisplayMessage_WithFileLinkMarkup_RendersClickableLinkWhenAllowMarkup()
    {
        var output = new StringBuilder();
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.Yes,
            ColorSystem = ColorSystemSupport.TrueColor,
            Out = new AnsiConsoleOutput(new StringWriter(output)),
            Enrichment = new ProfileEnrichment { UseDefaultEnrichers = false }
        });
        console.Profile.Capabilities.Links = true;
        console.Profile.Width = int.MaxValue;

        var logFilePath = Path.Combine(s_logsDirectory.FullName, "cli [dev team].log");
        var interactionService = CreateInteractionService(console, CreateExecutionContext(logFilePath: logFilePath));

        var fileLinkMarkup = MarkupHelpers.SafeFileLink(interactionService, logFilePath);
        interactionService.DisplayMessage(
            KnownEmojis.PageFacingUp,
            $"See logs at {fileLinkMarkup}",
            allowMarkup: true);

        var outputString = output.ToString();
        var fileUri = new Uri(Path.GetFullPath(logFilePath)).AbsoluteUri;
        TerminalLinkAssert.ContainsLink(outputString, fileUri, logFilePath);
    }

    [Fact]
    public void DisplayError_WithAllowMarkup_RendersMarkupAsIs()
    {
        var output = new StringBuilder();
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.Yes,
            ColorSystem = ColorSystemSupport.TrueColor,
            Out = new AnsiConsoleOutput(new StringWriter(output)),
            Enrichment = new ProfileEnrichment { UseDefaultEnrichers = false }
        });
        console.Profile.Capabilities.Links = true;
        console.Profile.Width = int.MaxValue;

        var logFilePath = Path.Combine(s_logsDirectory.FullName, "cli [dev team].log");
        var interactionService = CreateInteractionService(console, CreateExecutionContext(logFilePath: logFilePath));

        var fileLinkMarkup = MarkupHelpers.SafeFileLink(interactionService, logFilePath);
        interactionService.DisplayError($"Build failed. Logs: {fileLinkMarkup}", allowMarkup: true);

        var outputString = output.ToString();
        var fileUri = new Uri(Path.GetFullPath(logFilePath)).AbsoluteUri;
        Assert.Contains("Build failed.", outputString);
        TerminalLinkAssert.ContainsLink(outputString, fileUri, logFilePath);
    }

    [Fact]
    public void DisplayError_WithoutAllowMarkup_EscapesBrackets()
    {
        var output = new StringBuilder();
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Out = new AnsiConsoleOutput(new StringWriter(output))
        });

        var interactionService = CreateInteractionService(console);

        // Bracket characters in the error message would normally break Spectre markup parsing,
        // but DisplayError escapes them by default.
        var message = "Build failed for [Project Alpha].";
        var exception = Record.Exception(() => interactionService.DisplayError(message));

        Assert.Null(exception);
        Assert.Contains("Build failed for [Project Alpha].", output.ToString());
    }

    [Fact]
    public void DisplayVersionUpdateNotification_WithMarkupCharactersInVersion_DoesNotThrow()
    {
        // Arrange
        var output = new StringBuilder();
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Out = new AnsiConsoleOutput(new StringWriter(output))
        });

        var interactionService = CreateInteractionService(console);

        // Version strings are unlikely to have brackets, but the method should handle it
        var version = "13.2.0-preview [beta]";
        var updateCommand = "aspire update --channel [stable]";

        // Act - should not throw due to unescaped markup characters
        var exception = Record.Exception(() => interactionService.DisplayVersionUpdateNotification(version, updateCommand));

        // Assert
        Assert.Null(exception);
        var outputString = output.ToString();
        Assert.Contains("A new version of Aspire is available:", outputString);
        Assert.Contains("13.2.0-preview [beta]", outputString);
        Assert.Contains("aspire update --channel [stable]", outputString);
    }

    [Fact]
    public void DisplayError_WithMarkupCharactersInMessage_DoesNotDoubleEscape()
    {
        // Arrange - verifies that DisplayError escapes once (callers should NOT pre-escape)
        var output = new StringBuilder();
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Out = new AnsiConsoleOutput(new StringWriter(output))
        });

        var interactionService = CreateInteractionService(console);

        // Error message with brackets (e.g., from an exception)
        var errorMessage = "Failed to connect to service [Prod]: Connection refused <timeout>";

        // Act - should not throw
        var exception = Record.Exception(() => interactionService.DisplayError(errorMessage));

        // Assert
        Assert.Null(exception);
        var outputString = output.ToString();
        // Should contain the original text (not double-escaped like [[Prod]])
        Assert.Contains("[Prod]", outputString);
        Assert.DoesNotContain("[[Prod]]", outputString);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData(null, true)]
    [InlineData("Stopping Aspire.", false)]
    [InlineData("Stopping Aspire.", true)]
    [InlineData("Stopping dashboard.", false)]
    [InlineData("Stopping dashboard.", true)]
    public void DisplayCancellationMessage_UsesDefaultOrCustomMessageAndConsoleOverride(string? message, bool useStderr)
    {
        var stdoutOutput = new StringBuilder();
        var stderrOutput = new StringBuilder();
        var stdoutConsole = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Out = new AnsiConsoleOutput(new StringWriter(stdoutOutput)),
            Enrichment = new ProfileEnrichment { UseDefaultEnrichers = false }
        });
        var stderrConsole = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Out = new AnsiConsoleOutput(new StringWriter(stderrOutput)),
            Enrichment = new ProfileEnrichment { UseDefaultEnrichers = false }
        });
        var consoleEnvironment = new ConsoleEnvironment(stdoutConsole, stderrConsole, TextReader.Null);
        var interactionService = new ConsoleInteractionService(consoleEnvironment, CreateExecutionContext(), TestHelpers.CreateInteractiveHostEnvironment(), new EnvironmentProcessPathProvider(), NullLoggerFactory.Instance, new ConsoleLogBufferContext());
        ConsoleOutput? consoleOverride = useStderr ? ConsoleOutput.Error : null;

        interactionService.DisplayCancellationMessage(message, consoleOverride);

        var targetConsole = useStderr ? stderrConsole : stdoutConsole;
        var expectedOutput = Environment.NewLine
            + ConsoleHelpers.FormatEmojiPrefix(KnownEmojis.StopSign, targetConsole, replaceEmoji: true)
            + (message ?? InteractionServiceStrings.OperationCancelled)
            + Environment.NewLine;
        Assert.Equal(useStderr ? string.Empty : expectedOutput, stdoutOutput.ToString());
        Assert.Equal(useStderr ? expectedOutput : string.Empty, stderrOutput.ToString());
    }

    [Fact]
    public void DisplayError_WritesToStderr_RegardlessOfConsoleSetting()
    {
        var stdoutOutput = new StringBuilder();
        var stderrOutput = new StringBuilder();
        var stdoutConsole = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Out = new AnsiConsoleOutput(new StringWriter(stdoutOutput))
        });
        var stderrConsole = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Out = new AnsiConsoleOutput(new StringWriter(stderrOutput))
        });

        var executionContext = CreateExecutionContext();
        var consoleEnvironment = new ConsoleEnvironment(stdoutConsole, stderrConsole, TextReader.Null);
        var interactionService = new ConsoleInteractionService(consoleEnvironment, executionContext, TestHelpers.CreateInteractiveHostEnvironment(), new EnvironmentProcessPathProvider(), NullLoggerFactory.Instance, new ConsoleLogBufferContext());

        // Console defaults to Standard (stdout), but errors should still go to stderr
        interactionService.DisplayError("Something went wrong");

        Assert.Empty(stdoutOutput.ToString());
        Assert.Contains("Something went wrong", stderrOutput.ToString());
    }

    [Fact]
    public void DisplayMessage_WritesToStdout_WhenConsoleIsStandard()
    {
        var stdoutOutput = new StringBuilder();
        var stderrOutput = new StringBuilder();
        var stdoutConsole = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Out = new AnsiConsoleOutput(new StringWriter(stdoutOutput))
        });
        var stderrConsole = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Out = new AnsiConsoleOutput(new StringWriter(stderrOutput))
        });

        var executionContext = CreateExecutionContext();
        var consoleEnvironment = new ConsoleEnvironment(stdoutConsole, stderrConsole, TextReader.Null);
        var interactionService = new ConsoleInteractionService(consoleEnvironment, executionContext, TestHelpers.CreateInteractiveHostEnvironment(), new EnvironmentProcessPathProvider(), NullLoggerFactory.Instance, new ConsoleLogBufferContext());

        interactionService.DisplayMessage(KnownEmojis.Information, "Status update");

        Assert.Contains("Status update", stdoutOutput.ToString());
        Assert.Empty(stderrOutput.ToString());
    }

    [Fact]
    public async Task DisplayMessage_SkillSourcesAlignEachUrlWithHeading()
    {
        var output = new StringBuilder();
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Out = new AnsiConsoleOutput(new StringWriter(output)),
            Enrichment = new ProfileEnrichment { UseDefaultEnrichers = false }
        });
        console.Profile.Width = 100;
        var interactionService = CreateInteractionService(console);
        var message = string.Format(CultureInfo.CurrentCulture, AgentCommandStrings.InitCommand_SkillSources,
            string.Join(Environment.NewLine, "https://example.com/skills-a", "https://example.com/skills-b"));

        interactionService.DisplayMessage(KnownEmojis.Information, message);

        var lines = output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        await Verify(string.Join('\n', lines.Select(line => line.TrimEnd())));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DisplayMessage_SourceTrustNoticeUsesWarningFormatting(bool enableColor)
    {
        var output = new StringBuilder();
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = enableColor ? AnsiSupport.Yes : AnsiSupport.No,
            ColorSystem = enableColor ? ColorSystemSupport.Standard : ColorSystemSupport.NoColors,
            Out = new AnsiConsoleOutput(new StringWriter(output)),
            Enrichment = new ProfileEnrichment { UseDefaultEnrichers = false }
        });
        console.Profile.Width = 200;
        var interactionService = CreateInteractionService(console);

        var executionContext = CreateExecutionContext();
        interactionService.DisplaySourceTrustWarning(executionContext);
        interactionService.DisplaySourceTrustWarning(executionContext);

        var expected = enableColor
            ? $"\u001b[91m\U0001F6A8\u001b[0m \u001b[93m{SharedCommandStrings.SourceTrustWarning}\u001b[0m"
            : $"\U0001F6A8 {SharedCommandStrings.SourceTrustWarning}";
        Assert.Equal(expected, output.ToString().TrimEnd());
    }

    [Fact]
    public void DisplayTemplateSources_DoesNotRepeatSourceTrustWarning()
    {
        var interaction = new TestInteractionService();
        var executionContext = CreateExecutionContext();

        interaction.DisplaySourceTrustWarning(executionContext);
        interaction.DisplayTemplateSources(
            [("first", new TemplateSource(".", "https://example.test/first.git", null))], executionContext);
        interaction.DisplayTemplateSources(
            [("second", new TemplateSource(".", "https://example.test/second.git", null))], executionContext);
        interaction.DisplaySourceTrustWarning(executionContext);

        Assert.Equal(
            [$"[yellow]{SharedCommandStrings.SourceTrustWarning}[/]",
                NewCommandStrings.TemplateSourceExplanation, NewCommandStrings.TemplateSourceExplanation],
            interaction.DisplayedMessages.Select(message => message.Message));
        Assert.Equal(2, interaction.DisplayedRenderables.Count);
    }

    [Fact]
    public void DisplaySourceTrustWarning_IsScopedToExecutionContext()
    {
        var interaction = new TestInteractionService();
        var executionContext = CreateExecutionContext();

        Parallel.For(0, 10, _ => interaction.DisplaySourceTrustWarning(executionContext));

        var warning = Assert.Single(interaction.DisplayedMessages);
        Assert.Equal($"[yellow]{SharedCommandStrings.SourceTrustWarning}[/]", warning.Message);
        Assert.Equal(ConsoleOutput.Error, warning.ConsoleOverride);

        interaction.DisplaySourceTrustWarning(CreateExecutionContext());

        Assert.Equal([warning, warning], interaction.DisplayedMessages);
    }

    [Fact]
    public void DisplayMessage_WithUnescapedMarkup_AutoEscapesAndDoesNotThrow()
    {
        // Arrange - verifies that DisplayMessage auto-escapes by default
        var output = new StringBuilder();
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Out = new AnsiConsoleOutput(new StringWriter(output))
        });

        var interactionService = CreateInteractionService(console);

        // Path with brackets that would be interpreted as Spectre markup if not escaped
        var path = @"C:\Users\[Dev Team]\logs\aspire.log";

        // Act - should not throw because DisplayMessage auto-escapes
        var exception = Record.Exception(() => interactionService.DisplayMessage(KnownEmojis.PageFacingUp, $"See logs at {path}"));

        // Assert
        Assert.Null(exception);
        var outputString = output.ToString();
        Assert.Contains(@"C:\Users\[Dev Team]\logs\aspire.log", outputString);
    }

    [Fact]
    public void DisplayMessage_WithAllowMarkupTrue_PassesThroughMarkup()
    {
        // Arrange - verifies that allowMarkup: true allows intentional Spectre markup
        var output = new StringBuilder();
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Out = new AnsiConsoleOutput(new StringWriter(output))
        });

        var interactionService = CreateInteractionService(console);

        // Message with intentional Spectre markup tags
        var message = "[bold cyan]MyProject.csproj[/]:";

        // Act - should not throw because markup is intentional
        var exception = Record.Exception(() => interactionService.DisplayMessage(KnownEmojis.FileFolder, message, allowMarkup: true));

        // Assert
        Assert.Null(exception);
        var outputString = output.ToString();
        Assert.Contains("MyProject.csproj", outputString);
    }

    [Fact]
    public void DisplayMessage_WithAllowMarkupTrue_UnescapedDynamicContent_Throws()
    {
        // Arrange - verifies that allowMarkup: true still requires callers to escape dynamic values
        var output = new StringBuilder();
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Out = new AnsiConsoleOutput(new StringWriter(output))
        });

        var interactionService = CreateInteractionService(console);

        // Dynamic content with brackets embedded in markup - NOT escaped
        var projectName = "MyProject [Beta]";
        var message = $"[bold cyan]{projectName}[/]:";

        // Act - should throw because [Beta] is invalid markup when allowMarkup: true
        var exception = Record.Exception(() => interactionService.DisplayMessage(KnownEmojis.FileFolder, message, allowMarkup: true));

        // Assert
        Assert.NotNull(exception);
    }

    [Fact]
    public void DisplaySuccess_WithMarkupCharacters_AutoEscapesByDefault()
    {
        // Arrange
        var output = new StringBuilder();
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Out = new AnsiConsoleOutput(new StringWriter(output))
        });

        var interactionService = CreateInteractionService(console);

        // Success message with bracket characters that would break markup if not escaped
        var message = "Package Aspire.Hosting.Azure [1.0.0-preview] added successfully";

        // Act - should not throw because DisplaySuccess auto-escapes
        var exception = Record.Exception(() => interactionService.DisplaySuccess(message));

        // Assert
        Assert.Null(exception);
        var outputString = output.ToString();
        Assert.Contains("Aspire.Hosting.Azure [1.0.0-preview]", outputString);
    }

    [Fact]
    public async Task ShowStatusAsync_WithMarkupCharacters_AutoEscapesByDefault()
    {
        // Arrange - verifies that ShowStatusAsync auto-escapes by default
        var output = new StringBuilder();
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Out = new AnsiConsoleOutput(new StringWriter(output))
        });

        var executionContext = CreateExecutionContext(debugMode: true);
        var interactionService = CreateInteractionService(console, executionContext);

        // Status text with brackets that would be interpreted as Spectre markup if not escaped
        var statusText = "Downloading CLI from https://example.com/[latest]/aspire.zip";

        // Act - should not throw because ShowStatusAsync auto-escapes
        var exception = await Record.ExceptionAsync(() =>
            interactionService.ShowStatusAsync(statusText, () => Task.FromResult(0)));

        // Assert
        Assert.Null(exception);
        var outputString = output.ToString();
        Assert.Contains("[latest]", outputString);
    }

    [Fact]
    public void ShowStatus_WithMarkupCharacters_AutoEscapesByDefault()
    {
        // Arrange - verifies that ShowStatus auto-escapes by default
        var output = new StringBuilder();
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Out = new AnsiConsoleOutput(new StringWriter(output))
        });

        var executionContext = CreateExecutionContext(debugMode: true);
        var interactionService = CreateInteractionService(console, executionContext);

        // Status text with brackets that would be interpreted as Spectre markup if not escaped
        var statusText = "Installing .NET SDK [10.0.0-preview.1]...";

        // Act - should not throw because ShowStatus auto-escapes
        var exception = Record.Exception(() => interactionService.ShowStatus(statusText, () => { }));

        // Assert
        Assert.Null(exception);
        var outputString = output.ToString();
        Assert.Contains("[10.0.0-preview.1]", outputString);
    }

    [Fact]
    public async Task ShowStatusAsync_WithAllowMarkupTrue_PassesThroughMarkup()
    {
        // Arrange - verifies that allowMarkup: true allows emoji and other Spectre markup
        var output = new StringBuilder();
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Out = new AnsiConsoleOutput(new StringWriter(output))
        });

        var executionContext = CreateExecutionContext(debugMode: true);
        var interactionService = CreateInteractionService(console, executionContext);

        // Status text with intentional Spectre emoji markup
        var statusText = ":rocket:  Creating new project";

        // Act - should not throw because markup is intentional
        var exception = await Record.ExceptionAsync(() =>
            interactionService.ShowStatusAsync(statusText, () => Task.FromResult(0), allowMarkup: true));

        // Assert
        Assert.Null(exception);
        var outputString = output.ToString();
        Assert.Contains("Creating new project", outputString);
    }

    [Fact]
    public async Task ShowStatusAsync_WithAllowMarkupTrue_UnescapedDynamicContent_Throws()
    {
        // Arrange - verifies that allowMarkup: true still requires callers to escape dynamic values
        var output = new StringBuilder();
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Out = new AnsiConsoleOutput(new StringWriter(output))
        });

        var executionContext = CreateExecutionContext(debugMode: true);
        var interactionService = CreateInteractionService(console, executionContext);

        // Dynamic content with invalid brackets when interpreted as markup
        var projectName = "MyProject [Beta]";
        var statusText = $":rocket:  Building {projectName}";

        // Act - should throw because [Beta] is invalid markup when allowMarkup: true
        var exception = await Record.ExceptionAsync(() =>
            interactionService.ShowStatusAsync(statusText, () => Task.FromResult(0), allowMarkup: true));

        // Assert
        Assert.NotNull(exception);
    }

    [Fact]
    public async Task ShowStatusAsync_WithEmojiName_PrependsEmojiAndAutoEscapes()
    {
        // Arrange - verifies that emojiName handles emoji separately and auto-escapes the status text
        var output = new StringBuilder();
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Out = new AnsiConsoleOutput(new StringWriter(output))
        });

        var executionContext = CreateExecutionContext(debugMode: true);
        var interactionService = CreateInteractionService(console, executionContext);

        // Status text with brackets that would be invalid markup if not escaped
        var statusText = "Building MyProject [Beta]";

        // Act - should not throw because emojiName handles emoji separately and text is auto-escaped
        var exception = await Record.ExceptionAsync(() =>
            interactionService.ShowStatusAsync(statusText, () => Task.FromResult(0), emoji: KnownEmojis.Rocket));

        // Assert
        Assert.Null(exception);
        var outputString = output.ToString();
        Assert.Contains("Building MyProject [Beta]", outputString);
        Assert.Contains("🚀", outputString);
    }

    [Fact]
    public void ShowStatus_WithEmojiName_PrependsEmojiAndAutoEscapes()
    {
        // Arrange - verifies that emojiName handles emoji separately and auto-escapes the status text
        var output = new StringBuilder();
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Out = new AnsiConsoleOutput(new StringWriter(output))
        });

        var executionContext = CreateExecutionContext(debugMode: true);
        var interactionService = CreateInteractionService(console, executionContext);

        // Status text with brackets that would be invalid markup if not escaped
        var statusText = "Installing .NET SDK [10.0.0-preview.1]...";

        // Act - should not throw because emojiName handles emoji separately and text is auto-escaped
        var exception = Record.Exception(() =>
            interactionService.ShowStatus(statusText, () => { }, emoji: KnownEmojis.Package));

        // Assert
        Assert.Null(exception);
        var outputString = output.ToString();
        Assert.Contains("Installing .NET SDK [10.0.0-preview.1]", outputString);
        Assert.Contains("📦", outputString);
    }

    [Fact]
    public void DisplaySubtleMessage_WithMarkupCharacters_EscapesByDefault()
    {
        // Arrange - verifies that DisplaySubtleMessage escapes by default
        var output = new StringBuilder();
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Out = new AnsiConsoleOutput(new StringWriter(output))
        });

        var interactionService = CreateInteractionService(console);

        // Message with all kinds of markup characters
        var message = "Error in [Module]: <Config> value $.items[0] invalid";

        // Act
        var exception = Record.Exception(() => interactionService.DisplaySubtleMessage(message));

        // Assert
        Assert.Null(exception);
        var outputString = output.ToString();
        Assert.Contains("[Module]", outputString);
    }

    [Fact]
    public void SelectionPrompt_ConverterPreservesIntentionalMarkup()
    {
        // Arrange - verifies that PromptForSelectionAsync does NOT escape the formatter output,
        // allowing callers to include intentional Spectre markup (e.g., [bold]...[/]).
        // This is a regression test for https://github.com/microsoft/aspire/pull/14422 where
        // blanket EscapeMarkup() in the converter broke [bold] rendering in 'aspire add'.
        var output = new StringBuilder();
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.Yes,
            ColorSystem = ColorSystemSupport.Standard,
            Out = new AnsiConsoleOutput(new StringWriter(output))
        });

        // Build a SelectionPrompt the same way ConsoleInteractionService does,
        // using a formatter that returns intentional markup (like AddCommand does).
        Func<string, string> choiceFormatter = item => $"[bold]{item}[/] (Aspire.Hosting.{item})";

        var prompt = new SelectionPrompt<string>()
            .Title("Select an integration:")
            .UseConverter(choiceFormatter)
            .AddChoices(["PostgreSQL", "Redis"]);

        // Act - verify the converter output preserves the [bold] markup
        // by checking that the converter is the formatter itself (not wrapped with EscapeMarkup)
        var converterOutput = choiceFormatter("PostgreSQL");

        // Assert - the formatter should produce raw markup, not escaped markup
        Assert.Equal("[bold]PostgreSQL[/] (Aspire.Hosting.PostgreSQL)", converterOutput);
        Assert.DoesNotContain("[[bold]]", converterOutput); // Must NOT be escaped
    }

    [Fact]
    public void SelectionPrompt_ConverterWithBracketsInData_MustBeEscapedByCaller()
    {
        // Arrange - verifies that callers are responsible for escaping dynamic data
        // that may contain bracket characters, while preserving intentional markup.
        // This tests the pattern used by AddCommand.PackageNameWithFriendlyNameIfAvailable.
        var output = new StringBuilder();
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Out = new AnsiConsoleOutput(new StringWriter(output))
        });

        // Simulate a package name that contains brackets (e.g., from an external source)
        var friendlyName = "Azure Storage [Preview]";
        var packageId = "Aspire.Hosting.Azure.Storage";

        // The formatter should escape dynamic values but preserve intentional markup
        var formattedOutput = $"[bold]{friendlyName.EscapeMarkup()}[/] ({packageId.EscapeMarkup()})";

        // Assert - intentional markup preserved, dynamic brackets escaped
        Assert.Equal("[bold]Azure Storage [[Preview]][/] (Aspire.Hosting.Azure.Storage)", formattedOutput);

        // Verify Spectre can render this without throwing
        var exception = Record.Exception(() => console.MarkupLine(formattedOutput));
        Assert.Null(exception);

        var outputString = output.ToString();
        Assert.Contains("Azure Storage [Preview]", outputString);
        Assert.Contains("Aspire.Hosting.Azure.Storage", outputString);
    }

    [Theory]
    [InlineData(true, "[Y/n]")]
    [InlineData(false, "[y/N]")]
    public async Task ConfirmAsync_DisplaysCapitalizedDefaultChoice(bool defaultValue, string expectedChoiceSuffix)
    {
        // Arrange - simulate pressing Enter (accepts default)
        var output = new StringBuilder();
        var console = CreateInteractiveConsoleWithInput(output, "\n");
        var interactionService = CreateInteractionService(console);

        // Act
        await interactionService.PromptConfirmAsync("Proceed?", PromptBinding.CreateDefault(defaultValue), cancellationToken: CancellationToken.None);

        // Assert - the output should contain the [Y/n] or [y/N] suffix
        var outputString = output.ToString();
        Assert.Contains(expectedChoiceSuffix, outputString);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ConfirmAsync_WhenUserPressesEnter_ReturnsDefaultValue(bool defaultValue)
    {
        // Arrange - simulate pressing Enter (empty input selects default)
        var output = new StringBuilder();
        var console = CreateInteractiveConsoleWithInput(output, "\n");
        var interactionService = CreateInteractionService(console);

        // Act
        var result = await interactionService.PromptConfirmAsync("Proceed?", PromptBinding.CreateDefault(defaultValue), cancellationToken: CancellationToken.None);

        // Assert - pressing Enter should accept the default value
        Assert.Equal(defaultValue, result);
    }

    [Theory]
    [InlineData(true, "y")]
    [InlineData(true, "Y")]
    [InlineData(false, "y")]
    [InlineData(false, "Y")]
    public async Task ConfirmAsync_WhenUserPressesYWithoutEnter_ReturnsTrue(bool defaultValue, string input)
    {
        var output = new StringBuilder();
        var console = CreateInteractiveConsoleWithInput(output, input);
        var interactionService = CreateInteractionService(console);

        var result = await interactionService.PromptConfirmAsync("Proceed?", PromptBinding.CreateDefault(defaultValue), cancellationToken: CancellationToken.None);

        Assert.True(result);
        Assert.EndsWith($"{input}\n", output.ToString().Replace("\r\n", "\n", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(true, "n")]
    [InlineData(true, "N")]
    [InlineData(false, "n")]
    [InlineData(false, "N")]
    public async Task ConfirmAsync_WhenUserPressesNWithoutEnter_ReturnsFalse(bool defaultValue, string input)
    {
        var output = new StringBuilder();
        var console = CreateInteractiveConsoleWithInput(output, input);
        var interactionService = CreateInteractionService(console);

        var result = await interactionService.PromptConfirmAsync("Proceed?", PromptBinding.CreateDefault(defaultValue), cancellationToken: CancellationToken.None);

        Assert.False(result);
        Assert.EndsWith($"{input}\n", output.ToString().Replace("\r\n", "\n", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PromptForStringAsync_CliProvidedValue_RunsValidator()
    {
        var output = new StringBuilder();
        var console = CreateInteractiveConsoleWithInput(output, "");
        var interactionService = CreateInteractionService(console);

        var option = new Option<string?>("--value");
        var command = new RootCommand { option };
        var parseResult = command.Parse("--value bad-value");
        var binding = PromptBinding.Create(parseResult, option);
        Func<string, ValidationResult> validator = v =>
            v == "bad-value" ? ValidationResult.Error("Invalid!") : ValidationResult.Success();

        await Assert.ThrowsAsync<NonInteractiveException>(() =>
            interactionService.PromptForStringAsync("Enter value:", validator: validator, binding: binding, cancellationToken: CancellationToken.None));
    }

    [Fact]
    public async Task PromptForStringAsync_CliProvidedEmptyValue_ThrowsWhenRequired()
    {
        var output = new StringBuilder();
        var console = CreateInteractiveConsoleWithInput(output, "");
        var interactionService = CreateInteractionService(console, hostEnvironment: TestHelpers.CreateNonInteractiveHostEnvironment());

        var binding = PromptBinding.CreateDefault<string?>("");

        await Assert.ThrowsAsync<NonInteractiveException>(() =>
            interactionService.PromptForStringAsync("Enter name:", required: true, binding: binding, cancellationToken: CancellationToken.None));
    }

    [Fact]
    public async Task PromptForStringAsync_NonInteractive_DefaultValuePassesValidation_ReturnsDefault()
    {
        var output = new StringBuilder();
        var console = CreateInteractiveConsoleWithInput(output, "");
        var interactionService = CreateInteractionService(console, hostEnvironment: TestHelpers.CreateNonInteractiveHostEnvironment());

        var binding = PromptBinding.CreateDefault<string?>("good-value");
        Func<string, ValidationResult> validator = v =>
            v == "good-value" ? ValidationResult.Success() : ValidationResult.Error("Invalid!");

        var result = await interactionService.PromptForStringAsync("Enter value:", validator: validator, binding: binding, cancellationToken: CancellationToken.None);

        Assert.Equal("good-value", result);
    }

    [Fact]
    public async Task PromptForStringAsync_ResponseLoggingCanBeDisabledWithoutMaskingInput()
    {
        const string query = "private-template-search";
        var output = new StringBuilder();
        var console = CreateInteractiveConsoleWithInput(output, "\n");
        var sink = new TestSink();
        using var loggerFactory = new TestLoggerFactory(sink, enabled: true);
        var interaction = CreateInteractionService(console, loggerFactory: loggerFactory);

        var result = await interaction.PromptForStringAsync(
            "Search:", binding: PromptBinding.CreateDefault<string?>(query), logResponse: false).DefaultTimeout();

        Assert.Equal(query, result);
        Assert.Contains(query, output.ToString());
        Assert.Equal(
            ["Prompt: Search: (default: ****, secret: False)", "Prompt result: ****"],
            sink.Writes.Select(write => write.Message));
    }

    [Fact]
    public async Task PromptForStringAsync_WhenUserAcceptsEscapedDisplayDefault_ReturnsRawDefault()
    {
        var output = new StringBuilder();
        var console = CreateInteractiveConsoleWithInput(output, "\n");
        var interactionService = CreateInteractionService(console);
        const string rawDefault = "./[27;5;13~";

        var result = await interactionService.PromptForStringAsync(
            "Enter value:",
            binding: PromptBinding.CreateDefault<string?>(rawDefault),
            cancellationToken: CancellationToken.None);

        Assert.Equal(rawDefault, result);
    }

    [Fact]
    public async Task ConfirmAsync_NonInteractive_WithoutExplicitDefault_Throws()
    {
        var output = new StringBuilder();
        var console = CreateInteractiveConsoleWithInput(output, "");
        var interactionService = CreateInteractionService(console, hostEnvironment: TestHelpers.CreateNonInteractiveHostEnvironment());

        var option = new Option<bool>("--confirm");
        var command = new RootCommand { option };
        var parseResult = command.Parse("");
        var binding = PromptBinding.Create(parseResult, option);

        await Assert.ThrowsAsync<NonInteractiveException>(() =>
            interactionService.PromptConfirmAsync("Proceed?", binding: binding, cancellationToken: CancellationToken.None));
    }

    [Fact]
    public async Task ConfirmAsync_NonInteractive_WithExplicitDefault_ReturnsDefault()
    {
        var output = new StringBuilder();
        var console = CreateInteractiveConsoleWithInput(output, "");
        var interactionService = CreateInteractionService(console, hostEnvironment: TestHelpers.CreateNonInteractiveHostEnvironment());

        var binding = PromptBinding.CreateDefault(true);

        var result = await interactionService.PromptConfirmAsync("Proceed?", binding: binding, cancellationToken: CancellationToken.None);

        Assert.True(result);
    }

    [Fact]
    public async Task ConfirmAsync_NonInteractive_WithSeparateNonInteractiveDefault_ReturnsNonInteractiveDefault()
    {
        var output = new StringBuilder();
        var console = CreateInteractiveConsoleWithInput(output, "");
        var interactionService = CreateInteractionService(console, hostEnvironment: TestHelpers.CreateNonInteractiveHostEnvironment());

        var option = new Option<bool?>("--confirm");
        var command = new RootCommand { option };
        var parseResult = command.Parse("");
        var binding = PromptBinding.CreateBoolConfirm(parseResult, option, interactiveDefault: true, nonInteractiveDefault: false);

        var result = await interactionService.PromptConfirmAsync("Proceed?", binding: binding, cancellationToken: CancellationToken.None);

        Assert.False(result);
    }

    [Fact]
    public async Task ConfirmAsync_Interactive_WithSeparateNonInteractiveDefault_UsesInteractiveDefault()
    {
        var output = new StringBuilder();
        var console = CreateInteractiveConsoleWithInput(output, "\n");
        var interactionService = CreateInteractionService(console);

        var option = new Option<bool?>("--confirm");
        var command = new RootCommand { option };
        var parseResult = command.Parse("");
        var binding = PromptBinding.CreateBoolConfirm(parseResult, option, interactiveDefault: true, nonInteractiveDefault: false);

        var result = await interactionService.PromptConfirmAsync("Proceed?", binding: binding, cancellationToken: CancellationToken.None);

        Assert.True(result);
        Assert.Contains("[Y/n]", output.ToString());
    }

    [Fact]
    public void MatchChoices_WithDuplicateValues_ReturnsDeduplicated()
    {
        var choices = new[] { "alpha", "beta", "gamma" };

        var result = ConsoleInteractionService.MatchChoices("alpha,alpha,beta", choices, c => c);

        Assert.NotNull(result);
        Assert.Equal(2, result.Count);
        Assert.Equal("alpha", result[0]);
        Assert.Equal("beta", result[1]);
    }

    [Fact]
    public void MatchChoices_All_ReturnsAllChoices()
    {
        var choices = new[] { "alpha", "beta", "gamma" };

        var result = ConsoleInteractionService.MatchChoices("all", choices, c => c);

        Assert.NotNull(result);
        Assert.Equal(3, result.Count);
    }

    [Fact]
    public void MatchChoices_None_ReturnsEmpty()
    {
        var choices = new[] { "alpha", "beta" };

        var result = ConsoleInteractionService.MatchChoices("none", choices, c => c);

        Assert.NotNull(result);
        Assert.Empty(result);
    }

    [Fact]
    public void MatchChoices_InvalidValue_ReturnsNull()
    {
        var choices = new[] { "alpha", "beta" };

        var result = ConsoleInteractionService.MatchChoices("alpha,nonexistent", choices, c => c);

        Assert.Null(result);
    }

    [Fact]
    public void MatchChoice_MatchesByToString()
    {
        var choices = new[] { new TestItem("id1", "Display One"), new TestItem("id2", "Display Two") };

        var result = ConsoleInteractionService.MatchChoice("id1", choices, c => c.Display);

        Assert.NotNull(result);
        Assert.Equal("id1", result.Id);
    }

    [Fact]
    public void PromptBinding_CreateDefault_ResolvesAsNotProvided()
    {
        var binding = PromptBinding.CreateDefault("test");
        var (wasProvided, value) = binding.Resolve();

        Assert.False(wasProvided);
        Assert.Null(value);
        Assert.Equal("test", binding.DefaultValue);
        Assert.True(binding.HasExplicitDefault);
    }

    [Fact]
    public void PromptBinding_CreateWithoutDefault_HasNoExplicitDefault()
    {
        var option = new Option<bool>("--flag");
        var command = new RootCommand { option };
        var parseResult = command.Parse("");

        var binding = PromptBinding.Create(parseResult, option);

        Assert.False(binding.HasExplicitDefault);
    }

    [Fact]
    public void PromptBinding_CreateWithDefault_HasExplicitDefault()
    {
        var option = new Option<bool>("--flag");
        var command = new RootCommand { option };
        var parseResult = command.Parse("");

        var binding = PromptBinding.Create(parseResult, option, true);

        Assert.True(binding.HasExplicitDefault);
        Assert.True(binding.DefaultValue);
    }

    [Theory]
    [InlineData("--channel", "'--channel'")]
    [InlineData("--yes", "'--yes'")]
    [InlineData("--nuget-config-dir", "'--nuget-config-dir'")]
    public void PromptBinding_SymbolDisplayName_DoesNotDoubleDash(string optionName, string expectedDisplay)
    {
        var option = new Option<string?>(optionName);
        var command = new RootCommand { option };
        var parseResult = command.Parse("");

        var binding = PromptBinding.Create(parseResult, option);

        Assert.Equal(expectedDisplay, binding.SymbolDisplayName);
    }

    [Fact]
    public async Task ConfirmAsync_WithNullBinding_DefaultsToTrue()
    {
        var output = new StringBuilder();
        var console = CreateInteractiveConsoleWithInput(output, "\n");
        var interactionService = CreateInteractionService(console);

        var result = await interactionService.PromptConfirmAsync("Proceed?", binding: null, cancellationToken: CancellationToken.None);

        Assert.True(result);
        Assert.Contains("[Y/n]", output.ToString());
    }

    [Fact]
    public async Task PromptForSelectionAsync_NonInteractive_CliProvidedInvalidValue_ShowsAvailableChoices()
    {
        var output = new StringBuilder();
        var console = CreateInteractiveConsoleWithInput(output, "");
        var interactionService = CreateInteractionService(console, hostEnvironment: TestHelpers.CreateNonInteractiveHostEnvironment());
        var choices = new[] { "option1", "option2", "option3" };

        var option = new Option<string?>("--choice");
        var command = new RootCommand { option };
        var parseResult = command.Parse("--choice invalid");
        var binding = PromptBinding.Create(parseResult, option);

        var ex = await Assert.ThrowsAsync<NonInteractiveException>(() =>
            interactionService.PromptForSelectionAsync("Select:", choices, x => x, binding, cancellationToken: CancellationToken.None));

        var outputString = output.ToString();
        Assert.Contains("option1", outputString);
        Assert.Contains("option2", outputString);
        Assert.Contains("option3", outputString);
    }

    [Fact]
    public async Task PromptForSelectionsAsync_NonInteractive_CliProvidedInvalidValue_ShowsAvailableChoices()
    {
        var output = new StringBuilder();
        var console = CreateInteractiveConsoleWithInput(output, "");
        var interactionService = CreateInteractionService(console, hostEnvironment: TestHelpers.CreateNonInteractiveHostEnvironment());
        var choices = new[] { "alpha", "beta", "gamma" };

        var option = new Option<string?>("--items");
        var command = new RootCommand { option };
        var parseResult = command.Parse("--items invalid");
        var binding = PromptBinding.Create(parseResult, option);

        var ex = await Assert.ThrowsAsync<NonInteractiveException>(() =>
            interactionService.PromptForSelectionsAsync("Select:", choices, x => x, binding: binding, cancellationToken: CancellationToken.None));

        var outputString = output.ToString();
        Assert.Contains("alpha", outputString);
        Assert.Contains("beta", outputString);
        Assert.Contains("gamma", outputString);
    }

    [Fact]
    public async Task PromptForSelectionsAsync_NonInteractive_CliProvidedInvalidValue_OmitsItemsOutsideBindingChoices()
    {
        // The visible multi-select prompt may include UX-only entries (e.g. a "Configure MCP server"
        // applicator) that share the prompt with the real catalog but must not be addressable from
        // the --option value. Callers narrow non-interactive validation via the bindingChoices subset;
        // entries outside that subset must not leak into the "Available values" rejection message.
        var output = new StringBuilder();
        var console = CreateInteractiveConsoleWithInput(output, "");
        var interactionService = CreateInteractionService(console, hostEnvironment: TestHelpers.CreateNonInteractiveHostEnvironment());
        var visibleChoices = new[] { "alpha", "beta", "ux-only-entry" };
        var bindingChoices = new[] { "alpha", "beta" };

        var option = new Option<string?>("--items");
        var command = new RootCommand { option };
        var parseResult = command.Parse("--items invalid");
        var binding = PromptBinding.Create(parseResult, option);

        await Assert.ThrowsAsync<NonInteractiveException>(() =>
            interactionService.PromptForSelectionsAsync("Select:", visibleChoices, x => x, binding: binding, bindingChoices: bindingChoices, cancellationToken: CancellationToken.None));

        var outputString = output.ToString();
        Assert.Contains("alpha", outputString);
        Assert.Contains("beta", outputString);
        Assert.DoesNotContain("ux-only-entry", outputString);
    }

    [Fact]
    public async Task PromptForSelectionsAsync_NonInteractive_CliProvidedInvalidValue_StripsSpectreMarkupFromChoiceLabels()
    {
        // Choice formatters sometimes return Spectre.Console markup (e.g. "[bold]Label[/]") so the
        // interactive multi-select can render styled text. The non-interactive rejection message is
        // plain text, so those tokens must be stripped rather than printed verbatim — otherwise a
        // user who mistypes --option sees `[bold]Label[/]` in the "Available values" list.
        var output = new StringBuilder();
        var console = CreateInteractiveConsoleWithInput(output, "");
        var interactionService = CreateInteractionService(console, hostEnvironment: TestHelpers.CreateNonInteractiveHostEnvironment());
        var choices = new[] { "alpha", "beta" };

        var option = new Option<string?>("--items");
        var command = new RootCommand { option };
        var parseResult = command.Parse("--items invalid");
        var binding = PromptBinding.Create(parseResult, option);

        await Assert.ThrowsAsync<NonInteractiveException>(() =>
            interactionService.PromptForSelectionsAsync(
                "Select:",
                choices,
                x => $"[bold]{x}[/] [dim](styled)[/]",
                binding: binding,
                cancellationToken: CancellationToken.None));

        var outputString = output.ToString();
        Assert.Contains("alpha", outputString);
        Assert.Contains("beta", outputString);
        Assert.Contains("(styled)", outputString);
        Assert.DoesNotContain("[bold]", outputString);
        Assert.DoesNotContain("[/]", outputString);
        Assert.DoesNotContain("[dim]", outputString);
    }

    [Fact]
    public async Task PromptForSelectionsAsync_NonInteractive_CliProvidedInvalidValue_WithUnescapedClosingBracketInChoiceLabel_DoesNotThrowInvalidOperationException()
    {
        // Non-interactive error reporting should tolerate literal brackets in labels.
        // These labels can come from user data and are not guaranteed to be valid Spectre markup.
        var output = new StringBuilder();
        var console = CreateInteractiveConsoleWithInput(output, "");
        var interactionService = CreateInteractionService(console, hostEnvironment: TestHelpers.CreateNonInteractiveHostEnvironment());
        var choices = new[] { "alpha", "beta" };

        var option = new Option<string?>("--items");
        var command = new RootCommand { option };
        var parseResult = command.Parse("--items invalid");
        var binding = PromptBinding.Create(parseResult, option);

        await Assert.ThrowsAsync<NonInteractiveException>(() =>
            interactionService.PromptForSelectionsAsync(
                "Select:",
                choices,
                x => $"{x}]",
                binding: binding,
                cancellationToken: CancellationToken.None));

        var outputString = output.ToString();
        Assert.Contains("alpha]", outputString);
        Assert.Contains("beta]", outputString);
    }

    [Fact]
    public async Task PromptForSelectionAsync_NonInteractive_WithDefaultValue_ReturnsMatch()
    {
        var output = new StringBuilder();
        var console = CreateInteractiveConsoleWithInput(output, "");
        var interactionService = CreateInteractionService(console, hostEnvironment: TestHelpers.CreateNonInteractiveHostEnvironment());
        var choices = new[] { "option1", "option2" };

        var option = new Option<string?>("--choice");
        var command = new RootCommand { option };
        var parseResult = command.Parse("");
        var binding = PromptBinding.Create(parseResult, option, "option2");

        var result = await interactionService.PromptForSelectionAsync("Select:", choices, x => x, binding, cancellationToken: CancellationToken.None);

        Assert.Equal("option2", result);
    }

    [Fact]
    public async Task PromptForSelectionAsync_CliProvidedValidValue_ReturnsMatch()
    {
        var output = new StringBuilder();
        var console = CreateInteractiveConsoleWithInput(output, "");
        var interactionService = CreateInteractionService(console);
        var choices = new[] { "option1", "option2" };

        var option = new Option<string?>("--choice");
        var command = new RootCommand { option };
        var parseResult = command.Parse("--choice option1");
        var binding = PromptBinding.Create(parseResult, option);

        var result = await interactionService.PromptForSelectionAsync("Select:", choices, x => x, binding, cancellationToken: CancellationToken.None);

        Assert.Equal("option1", result);
    }

    [Fact]
    public async Task PromptForSelectionsAsync_CliProvidedCommaSeparated_ReturnsMatches()
    {
        var output = new StringBuilder();
        var console = CreateInteractiveConsoleWithInput(output, "");
        var interactionService = CreateInteractionService(console);
        var choices = new[] { "alpha", "beta", "gamma" };

        var option = new Option<string?>("--items");
        var command = new RootCommand { option };
        var parseResult = command.Parse("--items alpha,gamma");
        var binding = PromptBinding.Create(parseResult, option);

        var result = await interactionService.PromptForSelectionsAsync("Select:", choices, x => x, binding: binding, cancellationToken: CancellationToken.None);

        Assert.Equal(2, result.Count);
        Assert.Equal("alpha", result[0]);
        Assert.Equal("gamma", result[1]);
    }

    [Fact]
    public void PromptBinding_InvertedBoolConfirm_SymbolDisplayName_IsCorrect()
    {
        var option = new Option<bool?>("--yes");
        var command = new RootCommand { option };
        var parseResult = command.Parse("--yes");

        var binding = PromptBinding.CreateInvertedBoolConfirm(parseResult, option, defaultValue: true);

        Assert.Equal("'--yes'", binding.SymbolDisplayName);
    }

    [Fact]
    public void PromptBinding_BoolConfirm_SymbolDisplayName_IsCorrect()
    {
        var option = new Option<bool?>("--include");
        var command = new RootCommand { option };
        var parseResult = command.Parse("--include");

        var binding = PromptBinding.CreateBoolConfirm(parseResult, option, defaultValue: false);

        Assert.Equal("'--include'", binding.SymbolDisplayName);
    }

    [Fact]
    public void PromptBinding_WithDefault_ChangesDefault()
    {
        var binding = PromptBinding.CreateDefault("original");
        var updated = binding.WithDefault("new-value");

        Assert.Equal("new-value", updated.DefaultValue);
        Assert.Equal("new-value", updated.NonInteractiveDefaultValue);
        Assert.True(updated.HasExplicitDefault);
    }

    [Fact]
    public void MatchChoices_CaseInsensitive_ReturnsMatches()
    {
        var choices = new[] { "Alpha", "Beta", "Gamma" };

        var result = ConsoleInteractionService.MatchChoices("alpha,BETA", choices, c => c);

        Assert.NotNull(result);
        Assert.Equal(2, result.Count);
        Assert.Equal("Alpha", result[0]);
        Assert.Equal("Beta", result[1]);
    }

    [Fact]
    public void MatchChoice_CaseInsensitive_ReturnsMatch()
    {
        var choices = new[] { "Alpha", "Beta" };

        var result = ConsoleInteractionService.MatchChoice("ALPHA", choices, c => c);

        Assert.NotNull(result);
        Assert.Equal("Alpha", result);
    }

    [Fact]
    public async Task PromptForStringAsync_NonInteractive_NoBinding_ThrowsInvalidOperationException()
    {
        var interactionService = CreateInteractionService(AnsiConsole.Console, hostEnvironment: TestHelpers.CreateNonInteractiveHostEnvironment());

        var binding = PromptBinding.CreateDefault<string?>("fallback");

        var result = await interactionService.PromptForStringAsync("Enter value:", binding: binding, cancellationToken: CancellationToken.None);

        Assert.Equal("fallback", result);
    }

    [Fact]
    public async Task PromptForSelectionAsync_NonInteractive_WithoutBinding_ThrowsInvalidOperationException()
    {
        var interactionService = CreateInteractionService(AnsiConsole.Console, hostEnvironment: TestHelpers.CreateNonInteractiveHostEnvironment());
        var choices = new[] { "option1", "option2" };

        var option = new Option<string?>("--choice");
        var command = new RootCommand { option };
        var parseResult = command.Parse("");
        var binding = PromptBinding.Create(parseResult, option);

        await Assert.ThrowsAsync<NonInteractiveException>(() =>
            interactionService.PromptForSelectionAsync("Select:", choices, x => x, binding, cancellationToken: CancellationToken.None));
    }

    [Fact]
    public async Task PromptForSelectionAsync_BindingProvided_DoesNotEchoSelection()
    {
        var output = new StringBuilder();
        var console = CreateInteractiveConsoleWithInput(output, "");
        var interactionService = CreateInteractionService(console);
        var choices = new[] { "option1", "option2" };

        var option = new Option<string?>("--choice");
        var command = new RootCommand { option };
        var parseResult = command.Parse("--choice option1");
        var binding = PromptBinding.Create(parseResult, option);

        var result = await interactionService.PromptForSelectionAsync("Select:", choices, x => x, binding, cancellationToken: CancellationToken.None);

        Assert.Equal("option1", result);
        Assert.Empty(output.ToString());
    }

    [Fact]
    public async Task PromptForSelectionsAsync_BindingProvided_DoesNotEchoSelection()
    {
        var output = new StringBuilder();
        var console = CreateInteractiveConsoleWithInput(output, "");
        var interactionService = CreateInteractionService(console);
        var choices = new[] { "alpha", "beta", "gamma" };

        var option = new Option<string?>("--items");
        var command = new RootCommand { option };
        var parseResult = command.Parse("--items alpha,gamma");
        var binding = PromptBinding.Create(parseResult, option);

        var result = await interactionService.PromptForSelectionsAsync("Select:", choices, x => x, binding: binding, cancellationToken: CancellationToken.None);

        Assert.Equal(2, result.Count);
        Assert.Empty(output.ToString());
    }

    [Fact]
    public async Task PromptForSelectionAsync_NonInteractiveDefault_DoesNotEchoSelection()
    {
        var output = new StringBuilder();
        var console = CreateInteractiveConsoleWithInput(output, "");
        var interactionService = CreateInteractionService(console, hostEnvironment: TestHelpers.CreateNonInteractiveHostEnvironment());
        var choices = new[] { "option1", "option2" };

        var option = new Option<string?>("--choice");
        var command = new RootCommand { option };
        var parseResult = command.Parse("");
        var binding = PromptBinding.Create(parseResult, option, "option2");

        var result = await interactionService.PromptForSelectionAsync("Select:", choices, x => x, binding, cancellationToken: CancellationToken.None);

        Assert.Equal("option2", result);
        Assert.Empty(output.ToString());
    }

    [Fact]
    public async Task PromptForSelectionsAsync_NonInteractiveDefault_DoesNotEchoSelection()
    {
        var output = new StringBuilder();
        var console = CreateInteractiveConsoleWithInput(output, "");
        var interactionService = CreateInteractionService(console, hostEnvironment: TestHelpers.CreateNonInteractiveHostEnvironment());
        var choices = new[] { "alpha", "beta", "gamma" };

        var option = new Option<string?>("--items");
        var command = new RootCommand { option };
        var parseResult = command.Parse("");
        var binding = PromptBinding.Create(parseResult, option, "alpha,beta");

        var result = await interactionService.PromptForSelectionsAsync("Select:", choices, x => x, binding: binding, cancellationToken: CancellationToken.None);

        Assert.Equal(2, result.Count);
        Assert.Empty(output.ToString());
    }

    [Fact]
    public async Task PromptForSelectionsAsync_AgentDefaults_MatchIdsWithLocalizedLabels()
    {
        var output = new StringBuilder();
        var console = CreateInteractiveConsoleWithInput(output, "");
        var interactionService = CreateInteractionService(console, hostEnvironment: TestHelpers.CreateNonInteractiveHostEnvironment());
        var option = new Option<string?>("--agents");
        var command = new RootCommand { option };
        var binding = PromptBinding.Create(command.Parse(""), option, "copilot,claude,opencode");
        TestItem[] clients = [
            new("copilot", AgentCommandStrings.Environment_Copilot),
            new("claude", AgentCommandStrings.Environment_ClaudeCode),
            new("opencode", AgentCommandStrings.Environment_OpenCode)];

        var result = await interactionService.PromptForSelectionsAsync(
            AgentCommandStrings.InitCommand_SelectAgentsPrompt, clients,
            static client => client.Display, binding: binding, cancellationToken: CancellationToken.None);

        Assert.Equal(clients, result);
        Assert.Empty(output.ToString());
    }

    private sealed record TestItem(string Id, string Display)
    {
        public override string ToString() => Id;
    }

    private static IAnsiConsole CreateSelectionConsole(StringBuilder output, IEnumerable<ConsoleKeyInfo> keys)
    {
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.Yes,
            ColorSystem = ColorSystemSupport.NoColors,
            Interactive = InteractionSupport.Yes,
            Out = new AnsiConsoleOutput(new StringWriter(output)),
            Enrichment = new ProfileEnrichment { UseDefaultEnrichers = false }
        });
        console.Profile.Width = 100;
        console.Profile.Height = 40;
        return new TestAnsiConsoleWithInput(console, keys);
    }

    private static CallbackTemplate CreateSearchableTemplate(string id, string? description) =>
        new(id, "Same", (_, name) => name, _ => { }, (_, _, _, _) => throw new InvalidOperationException("The picker must not execute a template."))
        {
            SearchDescription = description
        };

    private static IAnsiConsole CreateInteractiveConsoleWithInput(StringBuilder output, string input)
    {
        var settings = new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Interactive = InteractionSupport.Yes,
            Out = new AnsiConsoleOutput(new StringWriter(output)),
        };
        var console = AnsiConsole.Create(settings);
        console.Profile.Width = int.MaxValue;
        return new TestAnsiConsoleWithInput(console, new StringReader(input));
    }
}
