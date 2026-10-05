// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Utils;

/// <summary>
/// Exposes the shared <c>PathNormalizer</c> helpers to test projects.
/// </summary>
/// <remarks>
/// Integration packages source-share <c>src/Shared/PathNormalizer.cs</c> rather than reaching into
/// Aspire.Hosting's internals, so a test project that sees both assemblies' internals ends up with two
/// equally accessible <c>Aspire.Hosting.Utils.PathNormalizer</c> types and cannot name either of them
/// (CS0433). Tests that need the canonicalization the product uses go through this forwarder, which is
/// unambiguous because it lives only here and still runs the one shared implementation.
/// </remarks>
public static class TestPathNormalizer
{
    /// <inheritdoc cref="PathNormalizer.ResolveSymlinks(string)"/>
    public static string ResolveSymlinks(string path) => PathNormalizer.ResolveSymlinks(path);
}
