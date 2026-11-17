// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Security.Cryptography;

namespace Aspire.Hosting;

internal static class TokenGenerator
{
    public static string GenerateToken()
    {
        // Generate a 128-bit entropy token.
        return Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
    }
}
