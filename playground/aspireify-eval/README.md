# Aspirify Eval Apps

These are **pre-aspirification** playground apps used to evaluate the `aspireify` skill.
They are intentionally NOT wired up with Aspire — the goal is to run `aspire init` on them
and have the agent use the `aspireify` skill to fully aspirify them.

## Apps

### dotnet-traditional/

A traditional .NET solution with a JS frontend, similar to a real-world LOB app.
See its README for architecture and manual setup instructions.

### polyglot/

A polyglot microservices app with multiple languages, no solution file.
See its README for architecture and manual setup instructions.

## Eval process

1. `cd` into either app directory
2. Run `aspire init`
3. Let the agent execute the `aspireify` skill
4. Score against the rubric in `EVAL-RUBRIC.md`

**Important**: The app READMEs only describe the "before" state (how to run manually).
The eval rubric is in this directory, NOT inside the app directories, so the agent
can't peek at the expected outcomes.
