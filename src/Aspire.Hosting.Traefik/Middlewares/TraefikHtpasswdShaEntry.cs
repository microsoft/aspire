// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Security.Cryptography;
using System.Text;
using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Traefik;

/// <summary>
/// An <c>htpasswd</c> entry in the SHA1 format, computed from a password parameter when the value is read:
/// <code>
/// admin:{SHA}W6ph5Mm5Pz8GgiULbPgzG37mj9g=
/// </code>
/// </summary>
/// <remarks>
/// SHA1 because it is the only <c>htpasswd</c> format Traefik accepts that .NET can produce without another
/// dependency (bcrypt and apr1 MD5-crypt aren't built in). It is a password hash, so a cryptographic hash is required,
/// but SHA1 is fast to brute-force, so this is only used in run mode. See
/// <see href="https://httpd.apache.org/docs/2.4/misc/password_encryptions.html"/>.
/// </remarks>
internal sealed class TraefikHtpasswdShaEntry(string user, ParameterResource password) : IValueProvider, IManifestExpressionProvider, IValueWithReferences
{
    // ReferenceExpression reads this when the expression is created, so it can't throw. It never reaches a manifest
    // or a published file: AddBasicAuth refuses plain passwords when publishing.
    /// <inheritdoc/>
    public string ValueExpression => $"{user}:{{SHA}}({password.ValueExpression})";

    /// <inheritdoc/>
    public IEnumerable<object> References => [password];

    /// <inheritdoc/>
    public async ValueTask<string?> GetValueAsync(CancellationToken cancellationToken = default)
    {
        var value = await password.GetValueAsync(cancellationToken).ConfigureAwait(false);
        if (value is null)
        {
            return null;
        }

        var hash = Convert.ToBase64String(SHA1.HashData(Encoding.UTF8.GetBytes(value)));
        return $"{user}:{{SHA}}{hash}";
    }
}
