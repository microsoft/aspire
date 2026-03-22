// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREUSERSECRETS001

using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;

namespace Aspire.Hosting.Tests.Utils;

internal sealed class MockUserSecretsManager : IUserSecretsManager
{
    public bool IsAvailable => true;

    public string FilePath => "/mock/path/secrets.json";

    public bool TrySetSecret(string name, string value) => true;

    public void GetOrSetSecret(IConfigurationManager configuration, string name, Func<string> valueGenerator)
    {
    }

    public Task SaveStateAsync(JsonObject state, CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }
}
