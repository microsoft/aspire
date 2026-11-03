// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Reflection;
using System.Text;
using Aspire.Cli.Interaction;
using Aspire.Cli.Utils;
using Spectre.Console;

namespace Aspire.Cli.Tests.Utils;

public class EmojiWidthTests
{
    public static TheoryData<string> KnownEmojiNames
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var f in typeof(KnownEmojis).GetFields(BindingFlags.Public | BindingFlags.Static))
            {
                if (f.FieldType == typeof(KnownEmoji) && f.GetValue(null) is KnownEmoji emoji)
                {
                    data.Add(emoji.Name);
                }
            }
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(KnownEmojiNames))]
    public void GetCellWidth_Emojis_ReturnMeasuredWidth(string emojiName)
    {
        // Arrange
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Out = new AnsiConsoleOutput(new StringWriter(new StringBuilder()))
        });

        // Act
        var width = EmojiWidth.GetCellWidth(emojiName, console);

        // Assert - Spectre 0.55.2+ correctly measures emoji widths
        Assert.InRange(width, 1, 2);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void FormatEmojiPrefix_WarningReservesAVisibleGap(bool replaceEmoji, bool suppressColor)
    {
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Out = new AnsiConsoleOutput(new StringWriter())
        });
        var expectedEmoji = replaceEmoji ? Emoji.Replace(":warning:")
            : suppressColor ? ":warning:" : "[yellow]:warning:[/]";

        var prefix = ConsoleHelpers.FormatEmojiPrefix(KnownEmojis.Warning, console, replaceEmoji, suppressColor);

        Assert.Equal($"{expectedEmoji}  ", prefix);
    }

    [Theory]
    [InlineData(40, "Use the Aspire plugin at your own\n    risk.\n    Review the source before enabling\n    it.")]
    [InlineData(100, "Use the Aspire plugin at your own risk.\n    Review the source before enabling it.")]
    public void CreateEmojiGrid_WarningKeepsAGutterAroundWrappedText(int width, string expectedMessage)
    {
        var output = new StringBuilder();
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Out = new AnsiConsoleOutput(new StringWriter(output))
        });
        console.Profile.Width = width;

        console.Write(ConsoleHelpers.CreateEmojiGrid(KnownEmojis.Warning, console,
            new Text("Use the Aspire plugin at your own risk.\nReview the source before enabling it.")));

        var lines = output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal($"{Emoji.Replace(":warning:")}  {expectedMessage}",
            string.Join('\n', lines.Select(line => line.TrimEnd())));
    }
}
