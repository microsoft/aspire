export default defineEventHandler(() => ({
    literal: process.env.MANAGED_LITERAL,
    graph: process.env.MANAGED_GRAPH,
    beforeResource: process.env.MANAGED_BEFORE_RESOURCE,
    callback: process.env.MANAGED_CALLBACK,
}));
