// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Packaging;

namespace Aspire.Cli.Integrations;

internal sealed record IntegrationIndexSourceContext(
    DirectoryInfo WorkingDirectory,
    IReadOnlyList<PackageChannel> Channels);
