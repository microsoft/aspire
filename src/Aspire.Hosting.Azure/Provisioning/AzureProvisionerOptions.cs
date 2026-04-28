// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.ComponentModel.DataAnnotations;

namespace Aspire.Hosting.Azure.Provisioning;

internal sealed class AzureProvisionerOptions
{
    public string? TenantId { get; set; }

    public string? SubscriptionId { get; set; }

    public string? ResourceGroup { get; set; }

    /// <summary>
    /// Gets or sets a prefix used in resource groups names created.
    /// </summary>
    public string? ResourceGroupPrefix { get; set; }

    public bool? AllowResourceGroupCreation { get; set; }

    public string? Location { get; set; }

    [AllowedValues([
        "AzureCli", "AzurePowerShell", "VisualStudio", "VisualStudioCode",
        "AzureDeveloperCli", "InteractiveBrowser", "Default"
    ])]
    public string CredentialSource { get; set; } = "Default";

    /// <summary>
    /// Gets or sets the timeout in seconds for credential process operations (e.g., az CLI, PowerShell).
    /// Increase this value if credential validation times out on machines where the Azure CLI is slow to respond.
    /// When not set, the Azure SDK's default timeout is used.
    /// </summary>
    [Range(5, 600)]
    public int? CredentialProcessTimeoutSeconds { get; set; }
}
