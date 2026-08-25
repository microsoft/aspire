import { createBuilder } from './.aspire/modules/aspire.mjs';

const builder = await createBuilder();

const backend = await builder.addContainer("backend", "nginx");
await backend.withHttpEndpoint({ name: "http", targetPort: 80 });
const insights = await builder.addAzureApplicationInsights("insights");
const vault = await builder.addAzureKeyVault("vault");
const secretParameter = await builder.addParameter("upstream-key", { secret: true });

const apim = await builder.addAzureApiManagement("apim", {
    publisherEmail: "api-owners@example.com",
});

const api = await apim.addApi("backend-api", backend, "backend");
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
