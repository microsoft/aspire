// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Confluent.Kafka.Tests;

internal sealed class CommonHelpers
{
    public const string TestingEndpoint = "localhost:9092";
    public const string TestingPassword = "p@ssw0rd1";

    /// <summary>
    /// A connection string in the shape produced by Aspire.Hosting.Kafka when the broker is password protected.
    /// </summary>
    public const string TestingSaslConnectionString =
        $"BootstrapServers={TestingEndpoint};SecurityProtocol=SaslPlaintext;SaslMechanism=Plain;SaslUsername=kafka;SaslPassword=\"{TestingPassword}\"";
}
