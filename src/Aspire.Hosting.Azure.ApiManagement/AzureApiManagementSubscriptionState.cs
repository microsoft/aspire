// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Azure;

/// <summary>
/// Specifies the state of an API Management subscription.
/// </summary>
public enum AzureApiManagementSubscriptionState
{
    /// <summary>
    /// The subscription is active.
    /// </summary>
    Active,

    /// <summary>
    /// The subscription is suspended.
    /// </summary>
    Suspended,

    /// <summary>
    /// The subscription was submitted for approval.
    /// </summary>
    Submitted,

    /// <summary>
    /// The subscription was rejected.
    /// </summary>
    Rejected,

    /// <summary>
    /// The subscription expired.
    /// </summary>
    Expired,

    /// <summary>
    /// The subscription was cancelled.
    /// </summary>
    Cancelled,
}
