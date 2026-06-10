// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Reflection;
using Aspire.Cli.Interaction;
using Spectre.Console;

namespace Aspire.Cli.Tests.Interaction;

public class KnownEmojisTests
{
    [Fact]
    public void AllKnownEmojis_MapToValidSpectreConsoleEmojis()
    {
        var fields = typeof(KnownEmojis)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.FieldType == typeof(KnownEmoji))
            .ToList();

        Assert.NotEmpty(fields);

        foreach (var field in fields)
        {
            var knownEmoji = (KnownEmoji)field.GetValue(null)!;
            var input = $":{knownEmoji.Name}:";
            var resolved = Emoji.Replace(input);

            Assert.True(resolved != input, $"KnownEmojis.{field.Name} with emoji name \"{knownEmoji.Name}\" is not recognized by Spectre.Console.");
        }
    }
}
