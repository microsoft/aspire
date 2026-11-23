// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Text.Json;
using Aspire.Cli.Resources;

namespace Aspire.Cli.Secrets;

internal sealed class InvalidSecretsFileException(string path, JsonException innerException)
    : Exception(string.Format(
        CultureInfo.CurrentCulture,
        SecretCommandStrings.InvalidSecretsFile,
        path,
        innerException.LineNumber + 1,
        innerException.BytePositionInLine + 1), innerException)
{
}
