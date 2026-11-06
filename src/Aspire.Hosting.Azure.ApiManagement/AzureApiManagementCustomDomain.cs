// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Azure;

internal sealed record AzureApiManagementCustomDomain(
    string Hostname,
    IAzureKeyVaultSecretReference Certificate,
    AzureApiManagementHostnameType Type,
    bool DefaultSslBinding,
    bool NegotiateClientCertificate);
