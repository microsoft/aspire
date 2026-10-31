// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
using System.Text.RegularExpressions;

namespace Aspire.Hosting.Utils;

/// <summary>
/// Class to parse container references (e.g. "mcr.microsoft.com/dotnet/sdk:8.0")
/// </summary>
internal sealed partial class ContainerReferenceParser
{
    public static bool IsValidTag(string tag)
    {
        if (tag.Length is 0 or > 128 || !IsTagStartCharacter(tag[0]))
        {
            return false;
        }

        foreach (var character in tag.AsSpan(1))
        {
            if (!IsTagCharacter(character))
            {
                return false;
            }
        }

        return true;

        static bool IsTagStartCharacter(char character)
            => char.IsAsciiLetterOrDigit(character) || character == '_';

        static bool IsTagCharacter(char character)
            => IsTagStartCharacter(character) || character is '.' or '-';
    }

    public static ContainerReference Parse(string input)
    {
        if (string.IsNullOrEmpty(input))
        {
            throw new ArgumentOutOfRangeException(nameof(input), "repository name must have at least one component");
        }

        var match = ImageNameRegex().Match(input);

        if (!match.Success)
        {
            throw new ArgumentOutOfRangeException(nameof(input), input, "invalid reference format: could not parse container image name");
        }

        return new(
            GetGroupValueOrDefault(match.Groups["registry"]),
            match.Groups["image"].Value,
            GetGroupValueOrDefault(match.Groups["tag"]),
            GetGroupValueOrDefault(match.Groups["digest"])
            );

        static string? GetGroupValueOrDefault(Group group)
            => group.Success ? group.Value : default;
    }

    // Based on https://github.com/microsoft/vscode-docker-extensibility/blob/46d92f93d620de74b6505bf9fc391af592fee2db/packages/vscode-container-client/src/utils/parseDockerLikeImageName.ts#L22
    // with addition of ipv6 registries
    [GeneratedRegex("^((?<registry>((localhost|\\[(?:[a-fA-F0-9:]+)\\]|([\\w-]+(\\.[\\w-]+)+))(:\\d+)?)|([\\w-]+:\\d+))\\/)?(?<image>[\\w-./<>]+)(:(?<tag>[\\w-.<>]+))?(@(?<digest>.+))?$")]
    private static partial Regex ImageNameRegex();
}

internal record struct ContainerReference(string? Registry, string Image, string? Tag, string? Digest)
{
}
