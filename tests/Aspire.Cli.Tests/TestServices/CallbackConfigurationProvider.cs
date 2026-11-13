// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Extensions.Configuration;

namespace Aspire.Cli.Tests.TestServices;

internal sealed class CallbackConfigurationProvider : ConfigurationProvider, IConfigurationSource
{
    public Action<string>? Reading { get; set; }

    public override bool TryGet(string key, out string? value)
    {
        Reading?.Invoke(key);
        return base.TryGet(key, out value);
    }

    public IConfigurationProvider Build(IConfigurationBuilder builder) => this;
}
