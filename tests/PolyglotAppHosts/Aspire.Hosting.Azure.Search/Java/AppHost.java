import aspire.*;

void main() throws Exception {
        var builder = DistributedApplication.CreateBuilder();
        var search = builder.addAzureSearch("search");
        search.withRoleAssignments(search, new AzureSearchRole[] { AzureSearchRole.SEARCH_SERVICE_CONTRIBUTOR, AzureSearchRole.SEARCH_INDEX_DATA_READER });
        builder.build().run();
    }
