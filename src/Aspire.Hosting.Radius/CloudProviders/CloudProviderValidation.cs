// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.RegularExpressions;

namespace Aspire.Hosting.Radius.CloudProviders;

/// <summary>
/// Lightweight syntactic validators for cloud-provider configuration inputs
/// (FR-007). Each helper throws <see cref="ArgumentException"/> with
/// <c>paramName</c> set so callers get the offending parameter in the
/// thrown message without bespoke wrapping at every call site.
/// </summary>
internal static partial class CloudProviderValidation
{
    internal static void ValidateGuid(string value, string paramName)
    {
        ArgumentException.ThrowIfNullOrEmpty(value, paramName);
        if (!Guid.TryParse(value, out _))
        {
            throw new ArgumentException($"Value '{value}' is not a valid GUID.", paramName);
        }
    }

    internal static void ValidateNonEmpty(string value, string paramName)
        => ArgumentException.ThrowIfNullOrEmpty(value, paramName);

    internal static void ValidateAwsAccountId(string value, string paramName)
    {
        ArgumentException.ThrowIfNullOrEmpty(value, paramName);
        if (!AwsAccountIdPattern().IsMatch(value))
        {
            throw new ArgumentException(
                $"AWS account ID '{value}' must be exactly 12 digits.", paramName);
        }
    }

    internal static void ValidateIamRoleArn(string value, string paramName)
    {
        ArgumentException.ThrowIfNullOrEmpty(value, paramName);
        if (!IamRoleArnPattern().IsMatch(value))
        {
            throw new ArgumentException(
                $"IAM role ARN '{value}' is not in the expected form 'arn:aws:iam::<account>:role/<name>'.",
                paramName);
        }
    }

    [GeneratedRegex(@"^\d{12}$")]
    private static partial Regex AwsAccountIdPattern();

    [GeneratedRegex(@"^arn:aws:iam::\d{12}:role/[\w+=,.@-]+$")]
    private static partial Regex IamRoleArnPattern();
}
