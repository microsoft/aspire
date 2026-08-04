// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREAZURE003 // AddSubnet / AzureSubnetResource are evaluation-only

var builder = DistributedApplication.CreateBuilder(args);

// VNet layout (mirrors AksDemo so the two playgrounds can be diffed cleanly):
//   10.100.0.0/16   - vnet (chosen to avoid the AKS default service CIDR 10.0.0.0/16)
//     10.100.0.0/22 - aks node pool subnet (1024 IPs - room for pods/nodes)
//     10.100.4.0/24 - public AGC frontend subnet (delegated to ServiceNetworking by AddLoadBalancer)
//     10.100.5.0/24 - admin AGC frontend subnet
var vnet = builder.AddAzureVirtualNetwork("vnet", "10.100.0.0/16");
var aksSubnet = vnet.AddSubnet("aks-nodes", "10.100.0.0/22");
var publicSubnet = vnet.AddSubnet("alb-public", "10.100.4.0/24");
var adminSubnet = vnet.AddSubnet("alb-admin", "10.100.5.0/24");

var aks = builder.AddAzureKubernetesEnvironment("aks")
                 .WithSubnet(aksSubnet)
                 .WithSystemNodePool("Standard_D2as_v5");

aks.AddNodePool("workload", "Standard_D2as_v5", minCount: 1, maxCount: 3);

var publicLb = aks.AddLoadBalancer("public", publicSubnet);
var adminLb = aks.AddLoadBalancer("admin", adminSubnet);

// Email used to register the ACME account with Let's Encrypt. Treat as a parameter so
// it can be supplied per-environment (`aspire deploy -p acme-email=...`) without burning
// it into source.
var acmeEmail = builder.AddParameter("acme-email");

// Install cert-manager via the typed API. This is the only difference from AksDemo:
// AksDemo wires the chart in by hand and pairs it with a manual cluster-issuer annotation,
// whereas here cert-manager and its ClusterIssuer are first-class resources in the model.
var certManager = aks.AddCertManager("cert-manager");

// A single Let's Encrypt production ClusterIssuer with an HTTP-01 solver. cert-manager
// satisfies the challenge by serving a token at /.well-known/acme-challenge/{token} on
// the same hostname being validated, which works for any AGC-assigned FQDN because port
// 80 is publicly reachable.
var letsEncrypt = certManager.AddIssuer("letsencrypt-prod")
                             .WithLetsEncryptProduction(acmeEmail)
                             .WithHttp01Solver();

var api = builder.AddProject<Projects.CertManagerDemo_ApiService>("api")
   .WithExternalHttpEndpoints();

// Public gateway: serves /api -> the api service, attached to the public AGC ALB.
// WithTls(letsEncrypt) creates an HTTPS listener AND adds the
// `cert-manager.io/cluster-issuer: letsencrypt-prod` annotation in one call. Once AGC
// assigns the gateway its <random>.fz<n>.alb.azure.com FQDN, the tls-fqdn-discovery
// pipeline step patches it into the listener and cert-manager issues a real Let's Encrypt
// cert via HTTP-01 against that FQDN. No ClusterIssuer needs to be created out-of-band.
aks.AddGateway("storefront-gw")
   .WithLoadBalancer(publicLb)
   .WithRoute("/api", api.GetEndpoint("http"))
   .WithTls(letsEncrypt);

// Admin gateway: same backend on a separate AGC ALB. No TLS here on purpose, so the diff
// between an HTTP-only and a cert-manager-managed HTTPS gateway is obvious side-by-side.
aks.AddGateway("admin-gw")
   .WithLoadBalancer(adminLb)
   .WithRoute("/admin", api.GetEndpoint("http"));

builder.Build().Run();
