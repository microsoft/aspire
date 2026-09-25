import { AzureSearchRole, createBuilder } from './.aspire/modules/aspire.mjs';

const builder = await createBuilder();
const search = await builder.addAzureSearch('search');
await search.configureInfrastructure(async infrastructure => {
    const service = await infrastructure.getSearchService();
    const tags = await service.tags.get();
    await tags.set("provisioning-proxy", "typescript");
});

await search.withSearchRoleAssignments(search, [
    AzureSearchRole.SearchServiceContributor,
    AzureSearchRole.SearchIndexDataReader
]);

await builder.build().run();
