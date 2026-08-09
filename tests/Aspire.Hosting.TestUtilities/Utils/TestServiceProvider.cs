// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.ComponentModel.Design;
using Aspire.Hosting.Dcp;
using Aspire.Hosting.Tests.Dcp;
using Microsoft.Extensions.Configuration;

namespace Aspire.Hosting.Tests.Utils;

public sealed class TestServiceProvider : IServiceProvider
{
    private readonly ServiceContainer _serviceContainer = new ServiceContainer();

    public TestServiceProvider(IConfiguration? configuration = null)
    {
        _serviceContainer.AddService(typeof(IDcpDependencyCheckService), new TestDcpDependencyCheckService());
        _serviceContainer.AddService(typeof(IConfiguration), configuration ?? new ConfigurationManager());
    }

    public object? GetService(Type serviceType)
    {
        return _serviceContainer.GetService(serviceType);
    }

    public TestServiceProvider AddService<TService>(TService instance)
        where TService : class
    {
        _serviceContainer.AddService(typeof(TService), instance);
        return this;
    }

    public TestServiceProvider AddService<TService>(ServiceCreatorCallback callback)
        where TService : class
    {
        _serviceContainer.AddService(typeof(TService), callback);
        return this;
    }

    public static IServiceProvider Instance { get; } = new TestServiceProvider();
}
