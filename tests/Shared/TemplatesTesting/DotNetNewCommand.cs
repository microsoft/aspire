// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Xunit;

namespace Aspire.Templates.Tests;

public class DotNetNewCommand : DotNetCommand
{
    private readonly string _customHive;

    public DotNetNewCommand(
        ITestOutputHelper _testOutput,
        bool useDefaultArgs = true,
        BuildEnvironment? buildEnv = null,
        string? hiveDirectory = null,
        string label = "dotnet-new")
            : base(_testOutput, useDefaultArgs, buildEnv, label)
    {
        string? hiveDir = hiveDirectory ?? _buildEnvironment.TemplatesCustomHive?.CustomHiveDirectory;
        if (hiveDir is null)
        {
            throw new ArgumentException("No custom hive directory was provided, and the BuildEnvironment does not have one set either");
        }
        _customHive = hiveDir;
    }

    protected override string GetFullArgs(params string[] args)
        // DefaultBuildArgs contains MSBuild properties for build, test, and run commands.
        // `dotnet new` treats those arguments as template options, so do not append them here.
        => $"new {string.Join(" ", args)} --debug:custom-hive \"{_customHive}\"";
}
