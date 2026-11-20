// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Xunit.Sdk;

// Resolving an Azure client through Microsoft.Extensions.Azure constructs an Azure.Core
// AzureEventSourceListener. Its constructor enumerates an internal List<EventSource> without
// synchronization, while the runtime calls the listener's OnEventSourceCreated (which can add to
// that list) from any thread that constructs a new EventSource. When tests run in parallel, another
// test lazily creating an EventSource (Azure-Core, Azure-Messaging-ServiceBus, Azure-Identity,
// Microsoft-Extensions-DependencyInjection, System.Net.*, ...) can therefore make the listener
// constructor throw "Collection was modified". Run the tests in this assembly sequentially so that
// no other test can create an EventSource while a client is being resolved.
// See https://github.com/microsoft/aspire/issues/18815.
[assembly: Parallelization(Mode = ParallelMode.None)]
