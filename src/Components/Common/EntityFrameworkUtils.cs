// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace Aspire;

internal static class EntityFrameworkUtils
{
    /// <summary>
    /// Binds the DbContext specific configuration section to settings when available.
    /// </summary>
    public static TSettings GetDbContextSettings<TContext, TSettings>(this IHostApplicationBuilder builder, string defaultConfigSectionName, string? connectionName, Action<TSettings, IConfiguration> bindSettings)
        where TSettings : new()
    {
        TSettings settings = new();
        var configurationSection = builder.Configuration.GetSection(defaultConfigSectionName);
        bindSettings(settings, configurationSection);
        // If the connectionName is not provided, we've been called in the context
        // of an Enrich invocation and don't need to bind the connectionName specific settings.
        // Instead, we'll just bind to the TContext-specific settings.
        if (connectionName is not null)
        {
            var connectionSpecificConfigurationSection = configurationSection.GetSection(connectionName);
            bindSettings(settings, connectionSpecificConfigurationSection);
        }
        var typeSpecificConfigurationSection = configurationSection.GetSection(typeof(TContext).Name);
        if (typeSpecificConfigurationSection.Exists()) // https://github.com/dotnet/runtime/issues/91380
        {
            bindSettings(settings, typeSpecificConfigurationSection);
        }

        return settings;
    }

    /// <summary>
    /// Ensures a <see cref="DbContext"/> is registered in DI.
    /// </summary>
    public static void CheckDbContextRegistered<TContext>(this IHostApplicationBuilder builder, [CallerMemberName] string memberName = "")
        where TContext : DbContext
    {
        if (!builder.Services.Any(sd => sd.ServiceType == typeof(DbContextOptions<TContext>)))
        {
            throw new InvalidOperationException($"DbContext<{typeof(TContext).Name}> was not registered. Ensure you have registered the DbContext in DI before calling {memberName}.");
        }
    }

    public static void EnsureDbContextNotRegistered<TContext>(this IHostApplicationBuilder builder, [CallerMemberName] string callerMemberName = "") where TContext : DbContext
    {
        if (!builder.Environment.IsDevelopment())
        {
            return;
        }

        var oldDbContextOptionsDescriptor = builder.Services.FirstOrDefault(sd => sd.ServiceType == typeof(DbContextOptions<TContext>));

        if (oldDbContextOptionsDescriptor is not null)
        {
            throw new InvalidOperationException($"DbContext<{typeof(TContext).Name}> is already registered. Please ensure 'services.AddDbContext<{typeof(TContext).Name}>()' is not used when calling '{callerMemberName}()' or use the corresponding 'Enrich' method.");
        }
    }
}
