// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Azure;

/// <summary>
/// Specifies which Microsoft accounts can sign in to an Entra ID application.
/// </summary>
/// <remarks>
/// <para>
/// The value must match the "Supported account types" setting of the app registration in the Azure portal.
/// The names match the <c>signInAudience</c> property of the Microsoft Graph
/// <a href="https://learn.microsoft.com/graph/api/resources/application">application</a> resource,
/// which stores that setting.
/// </para>
/// <para>
/// Microsoft.Identity.Web reads the sign-in audience from its <c>TenantId</c> setting. A single-tenant app
/// uses its home tenant there. The other audiences use the keywords <c>organizations</c>, <c>common</c>,
/// or <c>consumers</c> instead, and pass the home tenant as <c>AppHomeTenantId</c>.
/// </para>
/// </remarks>
public enum EntraIdSignInAudience
{
    /// <summary>
    /// Only work or school accounts in the app's home tenant can sign in. This is the default.
    /// </summary>
    AzureADMyOrg = 0,

    /// <summary>
    /// Work or school accounts in any Microsoft Entra tenant can sign in.
    /// </summary>
    AzureADMultipleOrgs,

    /// <summary>
    /// Work or school accounts in any Microsoft Entra tenant, and personal Microsoft accounts, can sign in.
    /// </summary>
    AzureADandPersonalMicrosoftAccount,

    /// <summary>
    /// Only personal Microsoft accounts, such as Outlook.com and Xbox accounts, can sign in.
    /// </summary>
    PersonalMicrosoftAccount
}
