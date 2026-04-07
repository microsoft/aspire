// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable AZPROVISION001 // Azure.Provisioning.Network is experimental
#pragma warning disable ASPIREAZURE003 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.

using Aspire.Hosting.Azure;
using Azure.Provisioning.Network;

var builder = DistributedApplication.CreateBuilder(args);

// Create a virtual network with two subnets:
// - One for the Container App Environment (with service delegation)
// - One for private endpoints
var vnet = builder.AddAzureVirtualNetwork("vnet");

var containerAppsSubnet = vnet.AddSubnet("container-apps", "10.0.0.0/23")
    .AllowInbound(port: "443", from: AzureServiceTags.AzureLoadBalancer, protocol: SecurityRuleProtocol.Tcp)
    .DenyInbound(from: AzureServiceTags.VirtualNetwork)
    .DenyInbound(from: AzureServiceTags.Internet);

// Create a NAT Gateway for deterministic outbound IP on the ACA subnet
var natGateway = builder.AddNatGateway("nat");
containerAppsSubnet.WithNatGateway(natGateway);

var privateEndpointsSubnet = vnet.AddSubnet("private-endpoints", "10.0.2.0/27")
    .AllowInbound(port: "443", from: AzureServiceTags.VirtualNetwork, protocol: SecurityRuleProtocol.Tcp)
    .DenyInbound(from: AzureServiceTags.Internet);

// Configure the Container App Environment to use the VNet
builder.AddAzureContainerAppEnvironment("env")
    .WithDelegatedSubnet(containerAppsSubnet);

var storage = builder.AddAzureStorage("storage").RunAsEmulator();

var blobs = storage.AddBlobs("blobs");
var mycontainer = storage.AddBlobContainer("mycontainer");

var queues = storage.AddQueues("queues");
var myqueue = storage.AddQueue("myqueue");

// Add private endpoints for blob and queue storage
// This automatically:
// - Creates Private DNS Zones for each service
// - Links the DNS zones to the VNet
// - Creates the Private Endpoints
// - Locks down public access to the storage account
privateEndpointsSubnet.AddPrivateEndpoint(blobs);
privateEndpointsSubnet.AddPrivateEndpoint(queues);

var sqlServer = builder.AddAzureSqlServer("sql")
    .RunAsContainer(c => c.WithLifetime(ContainerLifetime.Persistent));
privateEndpointsSubnet.AddPrivateEndpoint(sqlServer);

var db = sqlServer.AddDatabase("sqldb");

builder.AddProject<Projects.AzureVirtualNetworkEndToEnd_ApiService>("api")
       .WithExternalHttpEndpoints()
       .WithReference(db).WaitFor(db)
       .WithReference(mycontainer).WaitFor(mycontainer)
       .WithReference(myqueue).WaitFor(myqueue);

builder.Build().Run();
