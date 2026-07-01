import { createBuilder } from './.aspire/modules/aspire.mjs';
import { findRepoRoot } from './lib/repo.mjs';
import { addCliE2EImageWorkflow } from './workflows/cli-e2e-image.mjs';
import { addDailySmokeWorkflow } from './workflows/daily-smoke.mjs';
import { addDocsWorkflow } from './workflows/docs.mjs';
import { addFastWorkflow } from './workflows/fast.mjs';
import { addPackagingWorkflow } from './workflows/packaging.mjs';

const builder = await createBuilder();
const repoRoot = findRepoRoot(import.meta.url);
let pipeline = builder.pipeline();

pipeline = addFastWorkflow(pipeline, repoRoot);
pipeline = addPackagingWorkflow(pipeline, repoRoot);
pipeline = addDocsWorkflow(pipeline, repoRoot);
pipeline = addCliE2EImageWorkflow(pipeline, repoRoot);
pipeline = addDailySmokeWorkflow(pipeline, repoRoot);

await pipeline;
await builder.build().run();
