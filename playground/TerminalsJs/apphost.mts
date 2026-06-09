// Aspire TypeScript AppHost — TerminalsJs playground.
//
// Demonstrates WithTerminal() on a JavaScript-hosted resource from a polyglot
// AppHost. The guessing-game child app is started by node and attached to the
// dashboard's interactive terminal surface via xterm.js. Run with: `aspire run`.

import { createBuilder } from './.aspire/modules/aspire.mjs';

const builder = await createBuilder();

await builder
    .addNodeApp("guessing-game", "./guessing-game", "game.mjs")
    .withTerminal();

await builder.build().run();
