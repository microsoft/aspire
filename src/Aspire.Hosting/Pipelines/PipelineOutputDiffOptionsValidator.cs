// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Extensions.Options;

#pragma warning disable ASPIREPIPELINES005 // Internal consumer of the experimental output diffing API.

namespace Aspire.Hosting.Pipelines;

/// <summary>
/// Validates ordered output diff rules through the options pipeline.
/// </summary>
internal sealed class PipelineOutputDiffOptionsValidator : IValidateOptions<PipelineOutputDiffOptions>
{
    public ValidateOptionsResult Validate(string? name, PipelineOutputDiffOptions options)
    {
        if (!Enum.IsDefined(options.Mode))
        {
            return ValidateOptionsResult.Fail("Output diff mode must be Auto, Git, or FileSystem.");
        }
        foreach (var rule in options.Rules)
        {
            if (rule is null || string.IsNullOrWhiteSpace(rule.Pattern) || rule.Pattern.Contains('\0') ||
                Path.IsPathRooted(rule.Pattern) || rule.Pattern.Replace('\\', '/').Split('/').Contains(".."))
            {
                return ValidateOptionsResult.Fail("Output diff rules require a nonempty relative glob pattern without parent-directory segments.");
            }
            // Keep Action bindable as text: invalid enum conversion can silently drop collection
            // items. Options validation must see and reject malformed configuration instead.
            if (!string.Equals(rule.Action, "Include", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(rule.Action, "Exclude", StringComparison.OrdinalIgnoreCase))
            {
                return ValidateOptionsResult.Fail("Output diff rule actions must be Include or Exclude.");
            }
        }
        return ValidateOptionsResult.Success;
    }
}

#pragma warning restore ASPIREPIPELINES005
