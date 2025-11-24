// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

var builder = DistributedApplication.CreateBuilder(args);

var kafka = builder.AddKafka("kafka")
    .WithKafkaUI(kafkaUi => kafkaUi.WithHostPort(8080));

var schemaRegistry =
    kafka.WithKafkaSchemaRegistry(registry => registry.WithHostPort(7000),"schema-registry");

builder.AddProject<Projects.Producer>("producer")
    .WithReference(schemaRegistry)
    .WithReference(kafka).WaitFor(kafka)
    .WithArgs(kafka.Resource.Name);

builder.AddProject<Projects.Consumer>("consumer")
    .WithReference(kafka).WaitFor(kafka)
    .WithArgs(kafka.Resource.Name);

builder.AddKafka("kafka2").WithKafkaUI();
// This project is only added in playground projects to support development/debugging
// of the dashboard. It is not required in end developer code. Comment out this code
// or build with `/p:SkipDashboardReference=true`, to test end developer
// dashboard launch experience, Refer to Directory.Build.props for the path to
// the dashboard binary (defaults to the Aspire.Dashboard bin output in the
// artifacts dir).
#if !SKIP_DASHBOARD_REFERENCE
builder.AddProject<Projects.Aspire_Dashboard>(KnownResourceNames.AspireDashboard);
#endif

builder.Build().Run();
