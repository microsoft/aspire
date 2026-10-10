import { createBuilder } from './.aspire/modules/aspire.mjs';

await createBuilder();
throw new Error('Intentional native CLI guest failure.');
