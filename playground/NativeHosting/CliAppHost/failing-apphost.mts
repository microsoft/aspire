import { connect, createNativeBuilder } from './.aspire/modules/aspire.mjs';

await createNativeBuilder(await connect());
throw new Error('Intentional native CLI guest failure before graph startup.');
