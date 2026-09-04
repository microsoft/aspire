// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.RegularExpressions;

namespace Aspire.Hosting.MongoDB.Tests;

internal static partial class MongoDBTestHelpers
{
    /// <summary>
    /// Removes the trailing TLS flag from a MongoDB connection string expression.
    /// </summary>
    /// <remarks>
    /// MongoDB connection strings carry the <c>tls=true</c> flag as a conditional reference on the endpoint's TLS state, so
    /// that it resolves correctly no matter when TLS gets turned on. The generated name of such a reference embeds a hash,
    /// which makes it a poor thing to hard-code in assertions that are not about TLS in the first place.
    /// </remarks>
    public static string WithoutTlsFlag(string valueExpression) =>
        TlsConditionalReference().Replace(valueExpression, string.Empty);

    [GeneratedRegex(@"\{cond-[^}]+\.connectionString\}")]
    private static partial Regex TlsConditionalReference();
}
