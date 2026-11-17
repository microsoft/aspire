// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Agents.AspireSkills;
using Aspire.Cli.Configuration;
using Aspire.Cli.Packaging;
using Aspire.Cli.Templating;
using Microsoft.Extensions.Configuration;

namespace Aspire.Cli.Tests.Configuration;

public class SourceNameValidatorTests
{
    [Theory]
    [InlineData("a", true)]
    [InlineData("1", true)]
    [InlineData("Team_1-2", true)]
    [InlineData("Team.1-2_3", true)]
    [InlineData("default", true)]
    [InlineData("team.source", true)]
    [InlineData("team..source", true)]
    [InlineData("team.", true)]
    [InlineData("", false)]
    [InlineData(" ", false)]
    [InlineData("team source", false)]
    [InlineData(".team", false)]
    [InlineData("team:source", false)]
    [InlineData("-team", false)]
    [InlineData("_team", false)]
    [InlineData("team/source", false)]
    [InlineData("team\\source", false)]
    [InlineData("team\n", false)]
    [InlineData("\u00e9quipe", false)]
    [InlineData("\u0661team", false)]
    public void SourceNamesUseTheSameCharacterRules(string name, bool valid)
        => AssertSourceName(name, valid);

    [Theory]
    [InlineData(1, true)]
    [InlineData(64, true)]
    [InlineData(65, false)]
    public void SourceNamesUseTheSameLengthLimit(int length, bool valid)
    {
        AssertSourceName(new string('a', length), valid);
        AssertSourceName("a" + new string('.', length - 1), valid);
    }

    private static void AssertSourceName(string name, bool valid)
    {
        Assert.Equal(valid, SourceNameValidator.IsValid(name));
        using var configuration = new ConfigurationManager();
        configuration["integrationSources:default:enabled"] = "false";
        configuration["skillSources:default:enabled"] = "false";
        configuration[$"integrationSources:{name}:enabled"] = "false";
        configuration[$"skillSources:{name}:enabled"] = "false";
        if (valid)
        {
            TemplateSourceConfiguration.ValidateName(name);
            Assert.Empty(IntegrationSourceConfiguration.Resolve(configuration, new DirectoryInfo(Environment.CurrentDirectory)));
            Assert.Empty(SkillSource.Resolve(configuration));
        }
        else
        {
            Assert.Throws<TemplateSourceConfigurationException>(() => TemplateSourceConfiguration.ValidateName(name));
            Assert.Throws<InvalidOperationException>(() =>
                IntegrationSourceConfiguration.Resolve(configuration, new DirectoryInfo(Environment.CurrentDirectory)));
            Assert.Throws<InvalidOperationException>(() => SkillSource.Resolve(configuration));
        }
    }
}
