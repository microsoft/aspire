// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREAZURE003 // AddSubnet / AzureSubnetResource are evaluation-only

var builder = DistributedApplication.CreateBuilder(args);

// VNet layout:
//   10.0.0.0/16   - vnet
//     10.0.0.0/22 - aks node pool subnet (1024 IPs - room for pods/nodes)
//     10.0.4.0/24 - public AGC frontend subnet (delegated to ServiceNetworking by AddLoadBalancer)
//     10.0.5.0/24 - admin AGC frontend subnet
//
// AGC requires the ALB frontend subnet to be /24 or larger and to be delegated to
// Microsoft.ServiceNetworking/trafficControllers. AddLoadBalancer applies the delegation
// for us; we just need to make sure the AKS subnet and ALB subnets do not overlap.
var vnet = builder.AddAzureVirtualNetwork("vnet", "10.0.0.0/16");
var aksSubnet = vnet.AddSubnet("aks-nodes", "10.0.0.0/22");
var publicSubnet = vnet.AddSubnet("alb-public", "10.0.4.0/24");
var adminSubnet = vnet.AddSubnet("alb-admin", "10.0.5.0/24");

var aks = builder.AddAzureKubernetesEnvironment("aks")
                 .WithSubnet(aksSubnet);

// Two AGC ApplicationLoadBalancers. Each AGC ALB caps at 5 frontends, so production apps
// often need to spread Gateways/Ingresses across multiple LBs. This playground uses two
// just to exercise the multi-LB code path.
var publicLb = aks.AddLoadBalancer("public", publicSubnet);
var adminLb = aks.AddLoadBalancer("admin", adminSubnet);

var api = builder.AddProject<Projects.AksDemo_ApiService>("api");

// Public gateway: serves /api -> the api service, attached to the public AGC ALB.
// WithLoadBalancer attaches the alb.networking.azure.io association annotations and
// defaults the gatewayClassName to "azure-alb-external".
aks.AddGateway("storefront")
   .WithLoadBalancer(publicLb)
   .WithRoute("/api", api.GetEndpoint("http"));

// Admin gateway: serves the same backend but on a separate AGC ALB so a different set of
// network policies, frontends, or DNS names can hang off it.
aks.AddGateway("admin")
   .WithLoadBalancer(adminLb)
   .WithRoute("/admin", api.GetEndpoint("http"));

builder.Build().Run();
