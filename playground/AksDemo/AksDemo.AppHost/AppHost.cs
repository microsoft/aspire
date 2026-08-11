// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREAZURE003 // AddSubnet / AzureSubnetResource are evaluation-only

var builder = DistributedApplication.CreateBuilder(args);

// VNet layout:
//   10.100.0.0/16   - vnet (chosen to avoid the AKS default service CIDR 10.0.0.0/16)
//     10.100.0.0/22 - aks node pool subnet (1024 IPs - room for pods/nodes)
//     10.100.4.0/24 - public AGC frontend subnet (delegated to ServiceNetworking by AddLoadBalancer)
//     10.100.5.0/24 - admin AGC frontend subnet
//
// AGC requires the ALB frontend subnet to be /24 or larger and to be delegated to
// Microsoft.ServiceNetworking/trafficControllers. AddLoadBalancer applies the delegation
// for us; we just need to make sure the AKS subnet and ALB subnets do not overlap.
var vnet = builder.AddAzureVirtualNetwork("vnet", "10.100.0.0/16");
var aksSubnet = vnet.AddSubnet("aks-nodes", "10.100.0.0/22");
var publicSubnet = vnet.AddSubnet("alb-public", "10.100.4.0/24");
var adminSubnet = vnet.AddSubnet("alb-admin", "10.100.5.0/24");

var aks = builder.AddAzureKubernetesEnvironment("aks")
                 .WithSubnet(aksSubnet)
                 // Use the same AMD-based SKU as our AKS deployment E2E tests so this
                 // playground deploys consistently across regions and quotas.
                 .WithSystemNodePool("Standard_D2as_v5");

aks.AddNodePool("workload", "Standard_D2as_v5", minCount: 1, maxCount: 3);

// Two AGC ApplicationLoadBalancers. Each AGC ALB caps at 5 frontends, so production apps
// often need to spread Gateways/Ingresses across multiple LBs. This playground uses two
// just to exercise the multi-LB code path.
var publicLb = aks.AddLoadBalancer("public", publicSubnet);
var adminLb = aks.AddLoadBalancer("admin", adminSubnet);

var api = builder.AddProject<Projects.AksDemo_ApiService>("api")
   .WithExternalHttpEndpoints();

// Public gateway: serves /api -> the api service, attached to the public AGC ALB.
// WithLoadBalancer attaches the alb.networking.azure.io association annotations and
// defaults the gatewayClassName to "azure-alb-external".
//
// WithTls() (no hostname) creates an HTTPS listener that gets its hostname patched in
// by Aspire's tls-fqdn-discovery pipeline step once AGC assigns the gateway its
// <random>.fz<n>.alb.azure.com FQDN. The cert-manager.io/cluster-issuer annotation
// then triggers cert-manager to issue a real Let's Encrypt cert via HTTP-01 against
// that FQDN. A `letsencrypt-prod` ClusterIssuer needs to exist in the cluster.
aks.AddGateway("storefront-gw")
   .WithLoadBalancer(publicLb)
   .WithRoute("/api", api.GetEndpoint("http"))
   .WithTls()
   .WithGatewayAnnotation("cert-manager.io/cluster-issuer", "letsencrypt-prod");

// Admin gateway: serves the same backend but on a separate AGC ALB so a different set of
// network policies, frontends, or DNS names can hang off it.
aks.AddGateway("admin-gw")
   .WithLoadBalancer(adminLb)
   .WithRoute("/admin", api.GetEndpoint("http"));

// cert-manager installed via Helm so we can issue Let's Encrypt certificates for the AGC
// gateways via the HTTP-01 challenge. Gateway API support is enabled so cert-manager will
// watch Gateway listeners for TLS configuration and auto-issue Certificates.
//
// WithForceConflicts is needed because AKS clusters with the Azure Policy add-on (or
// Deployment Safeguards) install an `admissionsenforcer` field manager that mutates the
// cert-manager ValidatingWebhookConfiguration after the first install. Helm's SSA then
// fails the next upgrade with a conflict on .webhooks[*].namespaceSelector.
// WithForceConflicts adds --force-conflicts which tells SSA to take over the conflicting
// field non-destructively (no resources recreated).
aks.AddHelmChart("cert-manager", "oci://quay.io/jetstack/charts/cert-manager", "v1.18.2")
   .WithHelmValue("crds.enabled", "true")
   .WithHelmValue("config.apiVersion", "controller.config.cert-manager.io/v1alpha1")
   .WithHelmValue("config.kind", "ControllerConfiguration")
   .WithHelmValue("config.enableGatewayAPI", "true")
   .WithForceConflicts()
   .WithDestroy();

builder.Build().Run();
