// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using TypeScriptApiCompat;

var baselineOption = new Option<string>("--baseline")
{
    Description = "Directory containing base-branch *.ats.txt files.",
    Required = true
};

var currentOption = new Option<string>("--current")
{
    Description = "Directory containing current *.ats.txt files.",
    Required = true
};

var suppressionsRootOption = new Option<string?>("--suppressions-root")
{
    Description = "Repository root containing *.tscompat.suppression.txt files.",
    DefaultValueFactory = _ => Directory.GetCurrentDirectory()
};

var baselineSuppressionsRootOption = new Option<string?>("--baseline-suppressions-root")
{
    Description = "Target-branch repository root containing inherited suppression files."
};

var reportOption = new Option<string?>("--report")
{
    Description = "Write a Markdown report."
};

var githubAnnotationsOption = new Option<bool>("--github-annotations")
{
    Description = "Emit GitHub Actions ::error annotations."
};

var rootCommand = new RootCommand("Compare Aspire TypeScript/ATS API baselines.");
rootCommand.Options.Add(baselineOption);
rootCommand.Options.Add(currentOption);
rootCommand.Options.Add(suppressionsRootOption);
rootCommand.Options.Add(baselineSuppressionsRootOption);
rootCommand.Options.Add(reportOption);
rootCommand.Options.Add(githubAnnotationsOption);

rootCommand.SetAction(parseResult =>
{
    var options = new CommandLineOptions(
        parseResult.GetValue(baselineOption)!,
        parseResult.GetValue(currentOption)!,
        parseResult.GetValue(suppressionsRootOption)!,
        parseResult.GetValue(baselineSuppressionsRootOption),
        parseResult.GetValue(reportOption),
        parseResult.GetValue(githubAnnotationsOption));

    return TypeScriptApiCompatRunner.Run(options);
});

return rootCommand.Parse(args).Invoke();
