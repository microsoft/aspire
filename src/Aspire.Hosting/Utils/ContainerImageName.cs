// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.RegularExpressions;

namespace Aspire.Hosting.Utils;

internal static partial class ContainerImageName
{
    internal static ContainerReference ParseSource(string image)
    {
        ArgumentException.ThrowIfNullOrEmpty(image);

        // Parse registry:5000/team/image:tag@sha256:<hex>, including tag-plus-digest
        // references. The shared parser also accepts templates and opaque digests;
        // artifacts require actual registry references, not those looser inputs.
        // https://github.com/distribution/reference/blob/main/regexp.go
        if (image.Any(char.IsWhiteSpace) || !ContainerReferenceParser.TryParse(image, out var source))
        {
            throw new ArgumentException("The source must be a container image reference without a URL scheme or credentials.", nameof(image));
        }

        ValidateRepository(source.Image, nameof(image));
        if (source.Tag is not null)
        {
            ValidateTag(source.Tag, nameof(image));
        }
        if (source.Digest is not null && !DigestRegex().IsMatch(source.Digest))
        {
            throw new ArgumentException("The source digest must contain a SHA-256, SHA-384, or SHA-512 algorithm and its full hexadecimal digest.", nameof(image));
        }

        var registry = source.Registry?.ToLowerInvariant() ?? "docker.io";
        if (registry == "index.docker.io")
        {
            registry = "docker.io";
        }
        ValidateRegistry(registry, nameof(image));

        var repository = registry == "docker.io" && !source.Image.Contains('/')
            ? $"library/{source.Image}"
            : source.Image;
        if (registry.Length + repository.Length + 1 > 255)
        {
            throw new ArgumentException("The source registry and repository must not exceed 255 characters.", nameof(image));
        }

        return new(registry, repository, source.Tag ?? (source.Digest is null ? "latest" : null), source.Digest);
    }

    internal static void ValidateRepository(string repository, string parameterName)
    {
        ArgumentException.ThrowIfNullOrEmpty(repository, parameterName);
        if (repository.Length > 255 || !RepositoryRegex().IsMatch(repository))
        {
            throw new ArgumentException("The repository must be a lowercase relative container repository path, without a tag, digest, or URL scheme.", parameterName);
        }
    }

    internal static void ValidateTag(string tag, string parameterName)
    {
        ArgumentException.ThrowIfNullOrEmpty(tag, parameterName);
        if (!TagRegex().IsMatch(tag))
        {
            throw new ArgumentException("The image tag must contain 1 to 128 ASCII letters, digits, underscores, periods, or hyphens, and cannot start with a period or hyphen.", parameterName);
        }
    }

    internal static void ValidateRegistry(string registry, string parameterName)
    {
        if (string.IsNullOrEmpty(registry) || registry.IndexOfAny(['/', '\\', '?', '#', '@']) >= 0 ||
            !Uri.TryCreate($"https://{registry}/", UriKind.Absolute, out var uri) ||
            uri.UserInfo.Length != 0 || uri.AbsolutePath != "/" ||
            uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
            uri.Port is < 1 or > 65535 || uri.HostNameType == UriHostNameType.Unknown ||
            !string.Equals(uri.Authority, registry, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(uri.Authority + ":443", registry, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The registry must be a hostname with an optional port, without a URL scheme, path, or credentials.", parameterName);
        }
    }

    [GeneratedRegex(@"\A[a-z0-9]+(?:(?:[._]|__|-+)[a-z0-9]+)*(?:/[a-z0-9]+(?:(?:[._]|__|-+)[a-z0-9]+)*)*\z", RegexOptions.CultureInvariant)]
    private static partial Regex RepositoryRegex();

    [GeneratedRegex(@"\A[a-zA-Z0-9_][a-zA-Z0-9_.-]{0,127}\z", RegexOptions.CultureInvariant)]
    private static partial Regex TagRegex();

    [GeneratedRegex(@"\A(?:sha256:[a-f0-9]{64}|sha384:[a-f0-9]{96}|sha512:[a-f0-9]{128})\z", RegexOptions.CultureInvariant)]
    private static partial Regex DigestRegex();
}
