// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting;

/// <summary>
/// Represents ATS-compatible Azure AI Search roles.
/// </summary>
internal enum AzureSearchRole
{
    SearchIndexDataContributor,
    SearchIndexDataReader,
    SearchServiceContributor,
}
