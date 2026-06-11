// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text;
using System.Text.RegularExpressions;
using Aspire.TestUtilities;
using Aspire.Hosting;
using Docker.DotNet.Models;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Configurations;
using Testcontainers.Kafka;
using Xunit;
using Aspire.Components.Common.TestUtilities;

namespace Aspire.Confluent.Kafka.Tests;

public partial class KafkaContainerFixture : IAsyncLifetime
{
    private sealed partial class ConfluentLocalKafkaBuilder : ContainerBuilder<ConfluentLocalKafkaBuilder, KafkaContainer, KafkaConfiguration>
    {
        public const string KafkaImage = $"{ComponentTestConstants.AspireTestContainerRegistry}/{KafkaContainerImageTags.Image}:{KafkaContainerImageTags.Tag}";

        public const ushort KafkaPort = 9092;

        public const string StartupScriptFilePath = "/testcontainers.sh";

        public ConfluentLocalKafkaBuilder()
            : this(new KafkaConfiguration())
        {
            DockerResourceConfiguration = Init().DockerResourceConfiguration;
        }

        private ConfluentLocalKafkaBuilder(KafkaConfiguration resourceConfiguration)
            : base(resourceConfiguration)
        {
            DockerResourceConfiguration = resourceConfiguration;
        }

        protected override KafkaConfiguration DockerResourceConfiguration { get; }

        public override KafkaContainer Build()
        {
            Validate();
            return new KafkaContainer(DockerResourceConfiguration);
        }

        protected override ConfluentLocalKafkaBuilder Init()
        {
            return base.Init()
                .WithImage(KafkaImage)
                .WithPortBinding(KafkaPort, true)
                .WithEnvironment("KAFKA_LISTENERS", $"PLAINTEXT://localhost:29092,CONTROLLER://localhost:29093,PLAINTEXT_HOST://0.0.0.0:{KafkaPort}")
                .WithEnvironment("KAFKA_LISTENER_SECURITY_PROTOCOL_MAP", "CONTROLLER:PLAINTEXT,PLAINTEXT:PLAINTEXT,PLAINTEXT_HOST:PLAINTEXT")
                .WithEntrypoint("/bin/sh", "-c")
                .WithCommand("while [ ! -f " + StartupScriptFilePath + " ]; do sleep 0.1; done; " + StartupScriptFilePath)
                .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged(KafkaReadyRegex()))
                .WithStartupCallback((container, ct) =>
                {
                    const char lf = '\n';
                    var startupScript = new StringBuilder();
                    startupScript.Append("#!/bin/bash");
                    startupScript.Append(lf);
                    startupScript.Append($"export KAFKA_ADVERTISED_LISTENERS=PLAINTEXT://localhost:29092,PLAINTEXT_HOST://localhost:{container.GetMappedPublicPort(KafkaPort)}");
                    startupScript.Append(lf);
                    startupScript.Append("exec /etc/confluent/docker/run");
                    return container.CopyAsync(Encoding.Default.GetBytes(startupScript.ToString()), StartupScriptFilePath, fileMode: Unix.FileMode755, ct: ct);
                });
        }

        protected override ConfluentLocalKafkaBuilder Clone(IResourceConfiguration<CreateContainerParameters> resourceConfiguration)
        {
            return Merge(DockerResourceConfiguration, new KafkaConfiguration(resourceConfiguration));
        }

        protected override ConfluentLocalKafkaBuilder Clone(IContainerConfiguration resourceConfiguration)
        {
            return Merge(DockerResourceConfiguration, new KafkaConfiguration(resourceConfiguration));
        }

        protected override ConfluentLocalKafkaBuilder Merge(KafkaConfiguration oldValue, KafkaConfiguration newValue)
        {
            return new ConfluentLocalKafkaBuilder(new KafkaConfiguration(oldValue, newValue));
        }

        [GeneratedRegex(".*Transitioning from RECOVERY to RUNNING.*")]
        private static partial Regex KafkaReadyRegex();
    }

    public async ValueTask InitializeAsync()
    {
        if (RequiresFeatureAttribute.IsFeatureSupported(TestFeature.Docker))
        {
            Container = new ConfluentLocalKafkaBuilder().Build();
            await Container.StartAsync();
        }
    }

    public KafkaContainer? Container { get; private set; }

    public async ValueTask DisposeAsync()
    {
        if (Container is not null)
        {
            await Container.DisposeAsync();
        }
    }
}

[CollectionDefinition("Kafka Broker collection")]
public class KafkaBrokerCollection : ICollectionFixture<KafkaContainerFixture>
{

}
