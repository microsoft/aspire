import { createBuilder } from './.aspire/modules/aspire.mjs';

const builder = await createBuilder();

const backend = await builder.addContainer("backend", "nginx");
await backend.withHttpEndpoint({ name: "http", targetPort: 80 });

const apim = await builder.addAzureApiManagement("apim", {
    publisherEmail: "api-owners@example.com",
});

const api = await apim.addApi("backend-api", backend, "backend");
await api.withInboundPolicy('<set-header name="x-api" exists-action="override"><value>backend</value></set-header>');
await api.addOperation("get-backend", "GET", "/");

await builder.build().run();
