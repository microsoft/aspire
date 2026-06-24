// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Integrations;

internal static class IntegrationProviderTypes
{
    public const string NuGet = "nuget";
}

internal sealed record IntegrationProviderReference(string Type, string Package);
