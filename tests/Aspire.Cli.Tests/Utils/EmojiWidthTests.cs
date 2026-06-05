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
}
