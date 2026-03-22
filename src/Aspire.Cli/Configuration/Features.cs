// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Configuration;

internal sealed class Features(IConfiguration configuration, ILogger<Features> logger) : IFeatures
{
    public bool IsFeatureEnabled(string feature, bool defaultValue)
    {
        var configKey = $"features:{feature}";
        
        var value = configuration[configKey];
        
        logger.LogDebug("Feature check: {Feature}, ConfigKey: {ConfigKey}, Value: '{Value}', DefaultValue: {DefaultValue}",
            feature, configKey, value ?? "(null)", defaultValue);
        
        if (string.IsNullOrEmpty(value))
        {
            logger.LogDebug("Feature {Feature} using default value: {DefaultValue}", feature, defaultValue);
            return defaultValue;
        }
        
        var enabled = bool.TryParse(value, out var parsed) && parsed;
        logger.LogDebug("Feature {Feature} parsed value: {Enabled}", feature, enabled);
        return enabled;
    }
}