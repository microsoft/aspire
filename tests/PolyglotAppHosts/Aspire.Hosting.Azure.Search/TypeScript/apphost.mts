import { AzureSearchRole, createBuilder } from './.modules/aspire.mjs';

const builder = await createBuilder();
const search = await builder.addAzureSearch('search');

await search.withSearchRoleAssignments(search, [
    AzureSearchRole.SearchServiceContributor,
    AzureSearchRole.SearchIndexDataReader
]);

await builder.build().run();
