// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli;

// These config names don't include command arguments that are defined in commands themselves.
internal static class CliConfigNames
{
    public const string NoLogo = "ASPIRE_CLI_NOLOGO";
    public const string AppHostStartupTimeout = "ASPIRE_CLI_START_TIMEOUT";
    public const string InitSourceUrl = "ASPIRE_INIT_SOURCE_URL";
    public const string InitSourcePath = "ASPIRE_INIT_SOURCE_PATH";
    public const string InitSourceRef = "ASPIRE_INIT_SOURCE_REF";
}
