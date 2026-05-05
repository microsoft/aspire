# Hybrid design: rule-first config with project-aware outputs

## Purpose

This is the cleanest hybrid design for Aspire given that none of the current conditional test-selection work has merged yet.

The goal is to keep the useful hybrid behavior:

- rules can emit semantic tags for workflow jobs
- rules can directly select test projects
- selector output can still distinguish rule-selected projects from broader project discovery

At the same time, this version removes the old config split across `categories`, `tagRules`, `tagExpansions`, and `sourceToTestMappings`.

## Design position

The earlier hybrid draft preserved too much of the current shape.

If we are free to redesign, the cleaner contract is:

- one matching primitive: `rules`
- one place for universal ignore behavior: `ignore`
- one place for conservative fallback behavior: `fallbacks`
- one place for project-wide policy such as “do not include this project in default run-all”: `projectPolicies`
- one place for generic affected-project discovery that the selector may still use: `affectedProjectDiscovery`

That keeps the behavior hybrid without keeping two or three different rule languages.

## Pushback on `tagExpansions`

For the cases we care about right now, `tagExpansions` adds indirection without buying much:

- `extension/**` only needs to emit the `extension` tag
- polyglot workflow changes only need to emit the `polyglot` tag
- template paths can directly emit both the `templates` tag and `Aspire.Templates.Tests`
- simple `Aspire.{name}` mappings can directly resolve to a test project template

So this design intentionally does **not** include `tagExpansions`.

If we later find a real repeated bundle of projects that multiple tags need to share, we can add a reusable `projectSets` concept then.

## What makes this design hybrid

The hybrid boundary is in selector behavior, not in having multiple config sections that all match paths differently.

This design is hybrid because the selector can produce both:

1. projects selected directly by rules
2. projects discovered by repo-wide affected-project analysis

That means downstream consumers can still see:

- semantic tags for workflow gating
- direct rule-selected projects for special cases
- affected-project coverage from generic project discovery

## Core contract

```json
{
  "version": 1,
  "ignore": {
    "globs": [],
    "regexes": [],
    "tagsWhenIgnoredOnly": ["ignored_only"]
  },
  "affectedProjectDiscovery": {
    "include": [],
    "exclude": []
  },
  "projectPolicies": {},
  "fallbacks": {
    "triggerAll": {
      "globs": [],
      "regexes": [],
      "tags": ["run_all"]
    },
    "unmatched": {
      "mode": "run_all",
      "tags": ["run_all", "unmatched_fallback"]
    }
  },
  "rules": []
}
```

## Top-level sections

### `ignore`

Use `ignore` for paths that should not participate in selection at all.

- `globs` handles simple path patterns
- `regexes` handles exceptions such as “ignore all workflows except one”
- `tagsWhenIgnoredOnly` ensures ignored-only changes still emit at least one tag

### `affectedProjectDiscovery`

This keeps a first-class place for generic project discovery.

It is the main structural difference from the fully unified model.

The selector may still use these include/exclude patterns to derive `affectedProjects` from repo-wide project analysis, then union them with rule-selected projects.

### `projectPolicies`

`projectPolicies` holds project-wide behavior that must apply even when the project was not selected by a matching rule.

That is the right home for the template special case:

```json
{
  "projectPolicies": {
    "tests/Aspire.Templates.Tests/Aspire.Templates.Tests.csproj": {
      "runAllBehavior": "explicit_only"
    }
  }
}
```

That means:

- direct template-related changes can still select `Aspire.Templates.Tests`
- trigger-all and unmatched run-all do not include it by default
- if a trigger-all file also explicitly matches the template rule, the selector can still add the template project on top

### `fallbacks`

`triggerAll` is for known critical paths.

`unmatched` is the conservative backstop:

- if a file is not ignored
- and not matched by any rule
- then the selector emits `run_all`

### `rules`

Every rule:

- matches files via globs and optional regexes
- emits at least one tag
- can optionally emit direct projects
- can optionally emit templated projects such as `tests/Aspire.{name}.Tests/Aspire.{name}.Tests.csproj`

## Rule shape

```json
{
  "id": "aspire-named-tests",
  "description": "Simple Aspire.{name} mapping with exclusions.",
  "include": [
    "src/Aspire.{name}/**",
    "src/Components/Aspire.{name}/**",
    "tests/Aspire.{name}.Tests/**"
  ],
  "includeRegexes": [],
  "exclude": [],
  "excludeRegexes": [],
  "tags": [
    "integrations"
  ],
  "projects": [],
  "projectTemplates": [
    {
      "path": "tests/Aspire.{name}.Tests/Aspire.{name}.Tests.csproj",
      "ifExists": true
    }
  ]
}
```

Notes:

- `tags` is required and must be non-empty
- `projects` is for direct project paths
- `projectTemplates` is for `{name}`-style substitution
- matching a rule claims the file

## Key semantics

### 1. Every outcome emits at least one tag

- ignored-only changes emit `ignored_only`
- trigger-all emits `run_all` and `critical_path`
- unmatched fallback emits `run_all` and `unmatched_fallback`
- ordinary rules emit semantic tags such as `templates`, `integrations`, `extension`, or `polyglot`

### 2. Template tests are explicit-only by policy

`Aspire.Templates.Tests` is treated differently from the rest of the repo.

Its policy is:

- run when template-specific paths or files change
- do not participate in default run-all coverage

That policy is global, so it belongs in `projectPolicies`, not buried inside one rule hit.

### 3. Tag-only rules are valid

`extension/**` and polyglot workflow paths should not have to invent fake project mappings.

They can emit:

- `extension`
- `polyglot`

and let `tests.yml` use those tags to decide whether to run their corresponding jobs.

### 4. A trigger-all file can still coexist with explicit rule selection

If a changed file matches:

- a trigger-all path
- and a rule that selects an explicit-only project

the output should support both facts:

- `runAll = true`
- the explicit project is still present in `selectedTestProjects`

Downstream handling becomes:

1. run the default matrix because `runAll` is true
2. exclude anything listed in `runAllExcludedProjects`
3. add `selectedTestProjects` on top

### 5. Unmatched still means run all

This design keeps the conservative fallback.

If we fail to explain a non-ignored file with an explicit rule, we should run all tests rather than risk silent under-selection.

## Draft rules file

The concrete draft lives at:

- `eng/scripts/test-selection-rules.hybrid.json`

This file covers the requested cases:

- template-specific paths and files select `Aspire.Templates.Tests`
- `Aspire.Templates.Tests` is excluded from default run-all
- simple `src/Aspire.{name}` and `src/Components/Aspire.{name}` mappings resolve to matching test projects with exclusions
- universal ignores support both globs and regexes
- `extension/**` emits the `extension` tag
- polyglot workflow paths emit the `polyglot` tag
- trigger-all paths are explicit
- unmatched files still force run-all

## Simulated selector output

This output shape is intentionally explicit about the hybrid behavior:

```json
{
  "runAll": false,
  "reason": "selective",
  "selectedTags": [],
  "ruleSelectedProjects": [],
  "affectedProjects": [],
  "selectedTestProjects": [],
  "runAllExcludedProjects": [],
  "ruleHits": [],
  "ignoredFiles": [],
  "unmatchedFiles": []
}
```

`selectedTestProjects` is the union of:

- `ruleSelectedProjects`
- `affectedProjects`

When `runAll` is `true`, `runAllExcludedProjects` tells downstream jobs which projects are intentionally excluded from the default run-all set.

### 1. Template change

Changed file:

- `src/Aspire.ProjectTemplates/templates/aspire-starter/Program.cs`

```json
{
  "runAll": false,
  "reason": "selective",
  "selectedTags": [
    "templates"
  ],
  "ruleSelectedProjects": [
    "tests/Aspire.Templates.Tests/Aspire.Templates.Tests.csproj"
  ],
  "affectedProjects": [],
  "selectedTestProjects": [
    "tests/Aspire.Templates.Tests/Aspire.Templates.Tests.csproj"
  ],
  "runAllExcludedProjects": [],
  "ruleHits": [
    {
      "ruleId": "templates-explicit",
      "matchedFiles": [
        "src/Aspire.ProjectTemplates/templates/aspire-starter/Program.cs"
      ],
      "emittedTags": [
        "templates"
      ],
      "emittedProjects": [
        "tests/Aspire.Templates.Tests/Aspire.Templates.Tests.csproj"
      ]
    }
  ],
  "ignoredFiles": [],
  "unmatchedFiles": []
}
```

### 2. Simple `Aspire.{name}` mapping

Changed file:

- `src/Aspire.Dashboard/Model/DashboardClient.cs`

```json
{
  "runAll": false,
  "reason": "selective",
  "selectedTags": [
    "integrations"
  ],
  "ruleSelectedProjects": [
    "tests/Aspire.Dashboard.Tests/Aspire.Dashboard.Tests.csproj"
  ],
  "affectedProjects": [],
  "selectedTestProjects": [
    "tests/Aspire.Dashboard.Tests/Aspire.Dashboard.Tests.csproj"
  ],
  "runAllExcludedProjects": [],
  "ruleHits": [
    {
      "ruleId": "aspire-named-tests",
      "matchedFiles": [
        "src/Aspire.Dashboard/Model/DashboardClient.cs"
      ],
      "emittedTags": [
        "integrations"
      ],
      "resolvedProjectTemplates": [
        "tests/Aspire.Dashboard.Tests/Aspire.Dashboard.Tests.csproj"
      ]
    }
  ],
  "ignoredFiles": [],
  "unmatchedFiles": []
}
```

### 3. Extension change

Changed file:

- `extension/src/extension.ts`

```json
{
  "runAll": false,
  "reason": "selective",
  "selectedTags": [
    "extension"
  ],
  "ruleSelectedProjects": [],
  "affectedProjects": [],
  "selectedTestProjects": [],
  "runAllExcludedProjects": [],
  "ruleHits": [
    {
      "ruleId": "extension-job",
      "matchedFiles": [
        "extension/src/extension.ts"
      ],
      "emittedTags": [
        "extension"
      ]
    }
  ],
  "ignoredFiles": [],
  "unmatchedFiles": []
}
```

### 4. Pure trigger-all change

Changed file:

- `Aspire.slnx`

```json
{
  "runAll": true,
  "reason": "trigger_all",
  "selectedTags": [
    "run_all",
    "critical_path"
  ],
  "ruleSelectedProjects": [],
  "affectedProjects": [],
  "selectedTestProjects": [],
  "runAllExcludedProjects": [
    "tests/Aspire.Templates.Tests/Aspire.Templates.Tests.csproj"
  ],
  "ruleHits": [],
  "ignoredFiles": [],
  "unmatchedFiles": []
}
```

### 5. Trigger-all plus explicit template selection

Changed file:

- `global.json`

```json
{
  "runAll": true,
  "reason": "trigger_all",
  "selectedTags": [
    "run_all",
    "critical_path",
    "templates"
  ],
  "ruleSelectedProjects": [
    "tests/Aspire.Templates.Tests/Aspire.Templates.Tests.csproj"
  ],
  "affectedProjects": [],
  "selectedTestProjects": [
    "tests/Aspire.Templates.Tests/Aspire.Templates.Tests.csproj"
  ],
  "runAllExcludedProjects": [
    "tests/Aspire.Templates.Tests/Aspire.Templates.Tests.csproj"
  ],
  "ruleHits": [
    {
      "ruleId": "templates-explicit",
      "matchedFiles": [
        "global.json"
      ],
      "emittedTags": [
        "templates"
      ],
      "emittedProjects": [
        "tests/Aspire.Templates.Tests/Aspire.Templates.Tests.csproj"
      ]
    }
  ],
  "ignoredFiles": [],
  "unmatchedFiles": []
}
```

### 6. Unmatched change

Changed file:

- `eng/scripts/new-test-selector-experiment.csx`

```json
{
  "runAll": true,
  "reason": "unmatched",
  "selectedTags": [
    "run_all",
    "unmatched_fallback"
  ],
  "ruleSelectedProjects": [],
  "affectedProjects": [],
  "selectedTestProjects": [],
  "runAllExcludedProjects": [
    "tests/Aspire.Templates.Tests/Aspire.Templates.Tests.csproj"
  ],
  "ruleHits": [],
  "ignoredFiles": [],
  "unmatchedFiles": [
    "eng/scripts/new-test-selector-experiment.csx"
  ]
}
```

### 7. Ignored-only change

Changed file:

- `docs/ci/conditional-test-selection-hybrid.md`

```json
{
  "runAll": false,
  "reason": "ignored_only",
  "selectedTags": [
    "ignored_only"
  ],
  "ruleSelectedProjects": [],
  "affectedProjects": [],
  "selectedTestProjects": [],
  "runAllExcludedProjects": [],
  "ruleHits": [],
  "ignoredFiles": [
    "docs/ci/conditional-test-selection-hybrid.md"
  ],
  "unmatchedFiles": []
}
```

## Bottom line

This is the cleanest hybrid contract for the current ask:

- one rule language
- explicit project policy for the template special case
- tag-only workflow triggers where they make sense
- conservative unmatched fallback
- room for generic affected-project discovery without turning the config itself into two different rule systems
