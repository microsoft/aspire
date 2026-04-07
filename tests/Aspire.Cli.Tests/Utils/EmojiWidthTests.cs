// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Utils;
using Spectre.Console;
using System.Text;

namespace Aspire.Cli.Tests.Utils;

public class EmojiWidthTests
{
    [Theory]
    [InlineData("file_cabinet")]
    [InlineData("gear")]
    [InlineData("hammer_and_wrench")]
    [InlineData("information")]
    [InlineData("linked_paperclips")]
    [InlineData("warning")]
    public void GetCellWidth_TextPresentationEmojis_ReturnOne(string emojiName)
    {
        // Arrange - these emoji have Emoji_Presentation=No in Unicode.
        // Without FE0F (which Spectre strips), terminals render them as 1 cell.
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Out = new AnsiConsoleOutput(new StringWriter(new StringBuilder()))
        });

        // Act
        var width = EmojiWidth.GetCellWidth(emojiName, console);

        // Assert
        Assert.Equal(1, width);
    }

    [Theory]
    [InlineData("bug")]
    [InlineData("check_mark")]
    [InlineData("cross_mark")]
    [InlineData("file_folder")]
    [InlineData("hammer")]
    [InlineData("high_voltage")]
    [InlineData("locked_with_key")]
    [InlineData("magnifying_glass_tilted_left")]
    [InlineData("magnifying_glass_tilted_right")]
    [InlineData("microscope")]
    [InlineData("package")]
    [InlineData("rocket")]
    [InlineData("running_shoe")]
    [InlineData("stop_sign")]
    public void GetCellWidth_EmojiPresentationEmojis_ReturnMeasuredWidth(string emojiName)
    {
        // Arrange - these emoji have Emoji_Presentation=Yes in Unicode
        // and use Spectre's measured width (typically 2).
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Out = new AnsiConsoleOutput(new StringWriter(new StringBuilder()))
        });

        // Act
        var width = EmojiWidth.GetCellWidth(emojiName, console);

        // Assert - Spectre measurement for EP=Yes emoji is typically 2
        Assert.InRange(width, 1, 2);
    }
}
