// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// One-line "pit of success" AKS recipe — compare against CertManagerDemo to see how much
// boilerplate WithClusterDefaults removes.

var builder = DistributedApplication.CreateBuilder(args);

// ACME contact email; surfaced as a parameter so it can be supplied per-environment
// (`aspire deploy -p acme-email=...`) without burning it into source.
var acmeEmail = builder.AddParameter("acme-email");

// One call provisions:
//   - VNet (10.100.0.0/16) + AKS-node /22 + AGC /24
//   - System node pool (1-3 x Standard_D2as_v5)
//   - Public AGC load balancer
//   - cert-manager + Let's Encrypt production ClusterIssuer (HTTP-01 solver)
//   - Public Gateway attached to the load balancer with TLS, 301 HTTP->HTTPS and HSTS
//   - Auto-routes every external HTTP endpoint to that gateway under /{name}
//
// For dev loops that redeploy often, set AcmeEnvironment = Staging in a callback so we
// don't burn the LE production rate limit:
//
//   .WithClusterDefaults(acmeEmail, o => o.AcmeEnvironment = LetsEncryptEnvironment.Staging);
builder.AddAzureKubernetesEnvironment("aks")
       .WithClusterDefaults(acmeEmail);

// The auto-router picks this up because of WithExternalHttpEndpoints; no WithRoute needed.
builder.AddProject<Projects.AksClusterDefaultsDemo_ApiService>("api")
       .WithExternalHttpEndpoints();

builder.Build().Run();
