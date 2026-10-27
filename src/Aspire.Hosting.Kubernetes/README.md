# Kubernetes hosting integration

Use this integration to model, configure, and deploy Aspire compute resources to Kubernetes.

## Getting started

### Prerequisites

- [Helm](https://helm.sh/docs/intro/install/) **v4.2.0 or later** on your `PATH`.

Aspire shells out to `helm upgrade --install` to deploy the generated chart and validates the Helm version up front, so missing or older installs produce a clear actionable error instead of cryptic flag failures.

### Add the integration

From your AppHost directory, add the `Aspire.Hosting.Kubernetes` integration with the Aspire CLI:

```bash
aspire add Aspire.Hosting.Kubernetes
```

## Usage example

In the AppHost, add the environment:

**C#**

```csharp
builder.AddKubernetesEnvironment("k8s");
```

**TypeScript**

```typescript
await builder.addKubernetesEnvironment("k8s");
```

The dashboard container included in the published Helm chart opts out of dashboard usage and error telemetry by default (`ASPIRE_DASHBOARD_TELEMETRY_OPTOUT=true`). This does not affect the application's OpenTelemetry data sent to the dashboard.

### Container registries

Assign a registry to a project or Dockerfile-backed resource to include its registry-qualified image name in the published Helm chart:

**C#**

```csharp
var registry = builder.AddContainerRegistry("registry", "registry.example.com", "apps");

builder.AddProject<Projects.Api>("api")
    .WithContainerRegistry(registry);
```

**TypeScript**

```typescript
const registry = await builder.addContainerRegistry("registry", "registry.example.com", "apps");
const api = await builder.addNodeApp("api", "../api", "server.js");
await api.withContainerRegistry(registry);
```

For this example, `aspire publish` writes `registry.example.com/apps/api:latest` to the image parameter in `values.yaml`. Image push options that customize the remote image name or tag are also respected. Existing container images, such as `redis:7`, are unchanged.

Literal registry values and non-secret parameter defaults can be resolved during publishing. Registry values that require provisioning, secrets, or parameters without defaults remain deferred, with `<resource>:latest` as the published placeholder. `aspire deploy` resolves the image names again after provisioning and supplies them through a Helm values override file. If deploying the published chart yourself, supply the deferred image values to Helm.

### Volumes

Use a target-neutral volume when the Kubernetes environment's default storage policy is sufficient:

**C#**

```csharp
builder.AddProject<Projects.Api>("api")
    .WithVolume("data", "/data", env: "DATA_PATH");
```

**TypeScript**

```typescript
const api = await builder.addNodeApp("api", "../api", "server.js");
await api.withVolume("/data", "data", "DATA_PATH");
```

Projects and executables receive a workload-scoped Aspire store directory in run mode. The directory is reused across AppHost runs regardless of whether the process has a session or persistent lifetime. When published, the volume uses the Kubernetes environment's `DefaultStorageType`, which is `emptyDir` — storage lives for the lifetime of the pod and is lost when the pod restarts or is rescheduled. `DATA_PATH` contains `/data`. For storage that outlives the pod, use a [persistent volume](#persistent-volumes) instead.

### Persistent volumes

Add a persistent volume and expose its effective path through an environment variable:

**C#**

```csharp
var k8s = builder.AddKubernetesEnvironment("k8s");
var data = k8s.AddPersistentVolume("data")
    .WithCapacity("20Gi");

builder.AddProject<Projects.Api>("api")
    .WithPersistentVolume(data, "/data", env: "DATA_PATH");
```

**TypeScript**

```typescript
const k8s = await builder.addKubernetesEnvironment("k8s");
const data = await k8s.addPersistentVolume("data");
await data.withCapacity("20Gi");

const api = await builder.addNodeApp("api", "../api", "server.js");
await api.withKubernetesPersistentVolumeMount(data, "/data", { env: "DATA_PATH" });
```

When a project or executable runs locally, `DATA_PATH` points to a persistent directory in the AppHost's Aspire store. That store is normally under the AppHost intermediate-output directory, so cleaning build outputs can remove the local data. Local containers use a worktree-scoped container volume instead, provided the mount names an environment variable. Mounts that do not name one keep the persistent volume's own name for the local container volume, so data written by an earlier version of the AppHost stays attached. A single persistent-volume resource cannot be shared between local containers and local projects or executables because those execution types cannot use one backing store reliably.

When published or deployed, `DATA_PATH` contains `/data`. Applications can therefore use the same environment variable in both environments. The `isReadOnly` mount option is enforced after deployment, but Aspire cannot make a directory read-only for a process running directly on the host.

```shell
aspire publish -o k8s-artifacts
```

### ACME certificates with cert-manager

Install cert-manager and configure an issuer with the provider's ACME directory URL:

```csharp
var k8s = builder.AddKubernetesEnvironment("k8s");
var certManager = k8s.AddCertManager("cert-manager");

var letsEncrypt = certManager.AddIssuer("letsencrypt")
    .WithAcmeServer("https://acme-v02.api.letsencrypt.org/directory", "ops@contoso.com")
    .WithHttp01Solver();

// api must expose an external HTTP endpoint; the Gateway must be publicly reachable on port 80.
k8s.AddGateway("public")
    .WithGatewayClass("my-gateway-class")
    .WithRoute("/", api.GetEndpoint("http"))
    .WithTls(letsEncrypt);
```

The former `WithLetsEncryptProduction` and `WithLetsEncryptStaging` helpers have been removed.
Use `WithAcmeServer` with the corresponding URL instead; for TypeScript, use `withAcmeServer`
with either strings or parameters. Use staging for development and CI
to avoid production rate limits; staging certificates are not publicly trusted.

The directory URL can also be a non-secret parameter, so one AppHost can select an endpoint
per environment without changing source:

```csharp
var serverUrl = builder.AddParameter("acmeserver");
var email = builder.AddParameter("acmeemail");
var issuer = certManager.AddIssuer("acme")
    .WithAcmeServer(serverUrl, email)
    .WithHttp01Solver();
```

Set `Parameters__acmeserver` and `Parameters__acmeemail` before deployment.
The URL and email must either both be literal strings or both be parameters.
TypeScript uses the same `withAcmeServer(serverUrl, email)` method for both forms.

| Provider | ACME directory | External Account Binding |
|---|---|---|
| Let's Encrypt production | `https://acme-v02.api.letsencrypt.org/directory` | Not required |
| Let's Encrypt staging | `https://acme-staging-v02.api.letsencrypt.org/directory` | Not required |
| ZeroSSL | `https://acme.zerossl.com/v2/DV90` | Required |
| Google Trust Services production | `https://dv.acme-v02.api.pki.goog/directory` | Required |
| Google Trust Services staging | `https://dv.acme-v02.test-api.pki.goog/directory` | Required |

Other ACME providers, including SSL.com, Sectigo, and DigiCert, use the same APIs with
their account/product-specific directory URLs and EAB credentials. Provider-specific
authentication and credential acquisition remain outside Aspire. Private ACME servers
also require the appropriate TLS trust configuration; EAB does not establish that trust.

#### External Account Binding

[EAB is defined by RFC 8555, section 7.3.4](https://www.rfc-editor.org/rfc/rfc8555.html#section-7.3.4).
It binds a newly registered ACME account to an existing provider account using a key identifier
and an HMAC key. It does not replace the domain-ownership challenge.

For ZeroSSL, obtain EAB credentials from the provider and pass the HMAC key as a secret parameter:

```csharp
var keyId = builder.AddParameter("acmeeabkeyid");
var hmacKey = builder.AddParameter("acmeeabhmackey", secret: true);

var zeroSsl = certManager.AddIssuer("zerossl")
    .WithAcmeServer(serverUrl, email)
    .WithExternalAccountBinding(keyId, hmacKey)
    .WithHttp01Solver();
```

Both EAB arguments must be parameters; the HMAC parameter must be secret.
Set `Parameters__acmeserver` to `https://acme.zerossl.com/v2/DV90` for ZeroSSL.
In TypeScript:

```typescript
const keyId = await builder.addParameter("acmeeabkeyid");
const hmacKey = await builder.addParameter("acmeeabhmackey", { secret: true });
const serverUrl = await builder.addParameter("acmeserver");
const email = await builder.addParameter("acmeemail");
const issuer = await certManager.addIssuer("zerossl");
await issuer.withAcmeServer(serverUrl, email);
await issuer.withExternalAccountBinding(keyId, hmacKey);
await issuer.withHttp01Solver();
```

Supply parameters using your pipeline's secure environment-variable mechanism, with
`Parameters__acmeeabkeyid` and `Parameters__acmeeabhmackey` set before `aspire deploy`.
Never print the HMAC key or enable shell tracing around credential acquisition. Pass the
provider's unpadded base64url HMAC text unchanged; do not base64-decode it yourself.

Aspire applies an issuer-specific `<issuer>-eab` Secret before applying the ClusterIssuer.
The value is sent to `kubectl` through stdin, not command-line arguments or published
artifacts. Kubernetes' `Secret.data` base64 encoding is a separate layer, not encryption:
restrict Secret access with RBAC and configure encryption at rest as appropriate.
Existing Secrets not marked as Aspire cert-manager-managed are not adopted or deleted.
Destroy removes the managed EAB Secret before uninstalling cert-manager, even if EAB
was removed from the issuer configuration before redeployment; it does not
explicitly delete cert-manager's separate `<issuer>-account-key` Secret.
If EAB Secret cleanup fails, destroy logs a warning and continues with Helm uninstall
and infrastructure teardown. Remove any retained EAB Secret manually.
Changing issuer names or namespaces after deployment requires manual cleanup of
resources at their previous locations.

#### Deployment pipelines and account lifecycle

Credential lifetime and reuse are provider-specific. [ZeroSSL documents reusable EAB
credentials](https://zerossl.com/documentation/acme/). [Google Public CA](https://docs.cloud.google.com/certificate-manager/docs/public-ca-tutorial)
credentials must be used within seven days and can register one account. For a new Google
account, authenticate to Google in a separate pipeline step and acquire a fresh pair with
`gcloud publicca external-account-keys create`; capture `keyId` and `b64MacKey` securely
and supply them as the Aspire parameters. Aspire does not authenticate to providers,
refresh EAB credentials, or track their expiry.

Once registered, cert-manager renews using the ACME account private-key Secret, not by
creating a fresh EAB account each time. Preserve that Secret across redeployments.
Deleting/recreating the cluster or losing the account key may require fresh EAB credentials;
do not delete account keys as an automatic recovery shortcut. The managed EAB apply path
still requires configured parameter values on subsequent deployments.

`aspire deploy` applies the issuer but does not wait for ACME account registration. Check
issuer readiness before treating a deployment as ready for certificate issuance:

```shell
kubectl wait clusterissuer/zerossl --for=condition=Ready --timeout=300s
```

Readiness verifies account setup, not that a Gateway certificate has been issued.
Wait for the relevant Certificate separately.

See [cert-manager's ACME configuration](https://cert-manager.io/docs/configuration/acme/)
for account reuse and EAB details.

## Additional documentation

https://aspire.dev/integrations/gallery/
https://aspire.dev/integrations/compute/kubernetes/

## Feedback & contributing

https://github.com/microsoft/aspire
