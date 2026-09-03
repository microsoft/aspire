// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Radius.Secrets;

/// <summary>
/// Validates Kubernetes object names against the DNS-1123 rules Kubernetes enforces, so a malformed
/// name (e.g. from an existing-secret reference or a <c>SealedSecret</c> manifest) fails fast at the
/// API/publish boundary rather than only when <c>kubectl apply</c> / Radius rejects it at deploy.
/// See https://kubernetes.io/docs/concepts/overview/working-with-objects/names/.
/// </summary>
internal static class KubernetesName
{
    // DNS-1123 label: 1-63 chars, lowercase alphanumeric or '-', must start and end alphanumeric.
    public static bool IsDns1123Label(string value)
    {
        if (value.Length is 0 or > 63)
        {
            return false;
        }

        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            var isEdge = i == 0 || i == value.Length - 1;
            if (!(c is (>= 'a' and <= 'z') or (>= '0' and <= '9') || (!isEdge && c == '-')))
            {
                return false;
            }
        }

        return true;
    }

    // DNS-1123 subdomain: 1-253 chars, a dot-separated series of DNS-1123 labels.
    public static bool IsDns1123Subdomain(string value)
    {
        if (value.Length is 0 or > 253)
        {
            return false;
        }

        foreach (var label in value.Split('.'))
        {
            if (!IsDns1123Label(label))
            {
                return false;
            }
        }

        return true;
    }

    // A Kubernetes Secret data key must consist of alphanumeric characters, '-', '_', or '.'
    // A Kubernetes Secret/ConfigMap data key must be non-empty, at most 253 characters, match the
    // grammar `[-._a-zA-Z0-9]+`, and must not be "." or ".." or start with "..". This mirrors
    // apimachinery's IsConfigMapKey so a key that would be rejected by the Kubernetes API is caught
    // at publish time instead. See:
    // - https://kubernetes.io/docs/concepts/configuration/secret/
    // - https://github.com/kubernetes/apimachinery/blob/master/pkg/util/validation/validation.go (IsConfigMapKey)
    public static bool IsValidSecretDataKey(string value)
    {
        if (value.Length is 0 or > 253)
        {
            return false;
        }

        if (value is "." or ".." || value.StartsWith("..", StringComparison.Ordinal))
        {
            return false;
        }

        foreach (var c in value)
        {
            if (c is not ((>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '-' or '_' or '.'))
            {
                return false;
            }
        }

        return true;
    }
}
