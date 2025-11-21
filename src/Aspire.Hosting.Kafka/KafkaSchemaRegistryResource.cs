// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting;

/// <summary>
/// A resource that represents a Kafka UI container.
/// </summary>
/// <param name="name"></param>
public sealed class KafkaSchemaRegistryResource(string name) : ContainerResource(name);
