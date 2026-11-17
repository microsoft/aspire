// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Tests.Utils;

internal static class LineEndingAssert
{
    /// <summary>
    /// The line break that is not the one the CLI writes on this OS. A test that feeds it to a code path that
    /// rewrites line breaks can only pass if the path really converts it, instead of passing trivially on the
    /// OS where the input is already the OS line break.
    /// </summary>
    public static string NonOsNewLine { get; } = Environment.NewLine == "\n" ? "\r\n" : "\n";

    /// <summary>
    /// Asserts that a JSON object document uses only the OS line break and ends with exactly one newline after
    /// its closing brace, which is how the CLI writes JSON config files.
    /// </summary>
    public static void UsesOsLineBreaksAndSingleFinalNewline(string content)
    {
        Assert.Equal(content.ReplaceLineEndings(Environment.NewLine), content);
        Assert.EndsWith("}" + Environment.NewLine, content, StringComparison.Ordinal);
    }

    public static void FileUsesOsLineBreaksAndSingleFinalNewline(string path)
    {
        Assert.True(File.Exists(path), $"Expected file at {path}");
        UsesOsLineBreaksAndSingleFinalNewline(File.ReadAllText(path));
    }
}
