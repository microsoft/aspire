// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using static Aspire.Hosting.Utils.CommandLineArgsParser;

namespace Aspire.Hosting.Tests.Utils;

[Trait("Partition", "4")]
public class CommandLineArgsParserTests
{
    [Theory]
    [InlineData("", new string[] { })]
    [InlineData("single", new[] { "single" })]
    [InlineData("hello world", new[] { "hello", "world" })]
    [InlineData("foo bar baz", new[] { "foo", "bar", "baz" })]
    [InlineData("foo\tbar\tbaz", new[] { "foo", "bar", "baz" })]
    [InlineData("\"quoted string\"", new[] { "quoted string" })]
    [InlineData("\"quoted\tstring\"", new[] { "quoted\tstring" })]
    [InlineData("\"quoted \"\" string\"", new[] { "quoted \" string" })]
    // Single quotes are not treated as string delimiters
    [InlineData("\"hello 'world'\"", new[] { "hello 'world'" })]
    [InlineData("'single quoted'", new[] { "'single", "quoted'" })]
    [InlineData("'foo \"bar\" baz'", new[] { "'foo", "bar", "baz'" })]
    public void TestParse(string commandLine, string[] expectedParsed)
    {
        var actualParsed = Parse(commandLine);

        Assert.Equal(expectedParsed, actualParsed.ToArray());
    }

    [Theory]
    [InlineData("git", "git", new string[] { })]
    [InlineData("git status", "git", new[] { "status" })]
    [InlineData("dotnet run", "dotnet", new[] { "run" })]
    [InlineData("dotnet build --configuration Release", "dotnet", new[] { "build", "--configuration", "Release" })]
    [InlineData("\"C:\\Program Files\\Git\\bin\\git.exe\" status", "C:\\Program Files\\Git\\bin\\git.exe", new[] { "status" })]
    [InlineData("\"quoted exe\" arg1 arg2", "quoted exe", new[] { "arg1", "arg2" })]
    [InlineData("cmd /c \"echo hello world\"", "cmd", new[] { "/c", "echo hello world" })]
    [InlineData("python script.py --input \"file with spaces.txt\"", "python", new[] { "script.py", "--input", "file with spaces.txt" })]
    [InlineData("node\tapp.js\t--port\t3000", "node", new[] { "app.js", "--port", "3000" })]
    public void TestParseCommand(string commandLine, string expectedExe, string[] expectedArgs)
    {
        var (actualExe, actualArgs) = ParseCommand(commandLine);

        Assert.Equal(expectedExe, actualExe);
        Assert.Equal(expectedArgs, actualArgs);
    }
}
