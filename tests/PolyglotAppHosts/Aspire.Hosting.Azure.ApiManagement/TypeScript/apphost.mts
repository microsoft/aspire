import { createBuilder, refExpr } from './.aspire/modules/aspire.mjs';

const builder = await createBuilder();

const backend = await builder.addContainer("backend", "nginx");
await backend.withHttpEndpoint({ name: "http", targetPort: 80 });
const insights = await builder.addAzureApplicationInsights("insights");
const vault = await builder.addAzureKeyVault("vault");
const secretParameter = await builder.addParameter("upstream-key", { secret: true });

const apim = await builder.addAzureApiManagement("apim", {
    publisherEmail: "api-owners@example.com",
});

const foundry = await builder.addFoundry("foundry");
const model = await foundry.addDeployment(
    "chat",
    "gpt-5-mini",
    {
        modelVersion: "2025-08-07",
        format: "OpenAI",
    });
const foundryBackend = await apim.addFoundryBackend("foundry-backend", model);
const foundryPool = await apim.addBackendPool("foundry-pool");
await foundryPool.addBackendPoolMember(foundryBackend);
const openAIApi = await apim.addOpenAIApi("openai-api", "openai");
await openAIApi.withApiBackendPool(foundryPool);

const storage = await builder.addAzureStorage("storage");
const blobs = await storage.addBlobs("blobs");
const blobBackend = await apim.addBlobStorageBackend("blob-backend", blobs);
const blobApi = await apim.addApiWithoutTarget("blob-api", "blobs");
await blobApi.withApiBackend(blobBackend);

const api = await apim.addApi("backend-api", backend, "backend");
const primaryBackend = await apim.addBackend(
    "primary-backend",
    refExpr`https://primary.example.com`,
    {
        options: {
            managedIdentityResource: "api://example-backend",
            circuitBreaker: {
                name: "serverErrors",
                failureIntervalSeconds: 30,
                tripDurationSeconds: 60,
                statusCodeRanges: [{ minimum: 500, maximum: 599 }],
            },
        },
    });
const secondaryBackend = await apim.addBackend(
    "secondary-backend",
    refExpr`https://secondary.example.com`,
    {
        options: {
            managedIdentityResource: "api://example-backend",
            validateCertificateName: false,
        },
    });
const pool = await apim.addBackendPool("backend-pool");
await pool.addBackendPoolMember(primaryBackend, { weight: 3 });
await pool.addBackendPoolMember(secondaryBackend);
const pooledApi = await apim.addApiWithoutTarget("pooled-api", "pooled");
await pooledApi.withApiBackendPool(pool);
const fragment = await apim.addPolicyFragment(
    "standard-header",
    '<set-header name="x-api" exists-action="override"><value>backend</value></set-header>');
await apim.withInboundPolicyFragment(fragment);
await apim.withApplicationInsights(insights);
await apim.addNamedValue("region", "westus3");
await apim.addSecretNamedValue("upstream-key-value", secretParameter);
await apim.addKeyVaultNamedValue("shared-secret", vault, "shared-secret");
await apim.withCustomDomain("api.contoso.example", vault, "gateway-certificate");

await api.withInboundPolicyFragment(fragment);
await api.withApplicationInsights(insights);
const operation = await api.addOperation("get-backend", "GET", "/");
await operation.withInboundPolicyFragment(fragment);

const product = await apim.addProduct("backend-product", "Backend product");
await product.withApi(api);
await product.addSubscription("backend-client", "Backend client");

await builder.build().run();
