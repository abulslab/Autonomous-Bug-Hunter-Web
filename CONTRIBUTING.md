# Contributing

Thanks for helping. Bug Hunter's value is that its findings can be trusted, so contributions are judged first on
whether they keep that true.

## Ground rules

1. **AI proposes, the engine verifies.** A model may choose what to test and suggest what should be true. Whether
   something is a bug is decided by deterministic code on recorded evidence. A change that lets a model's opinion
   become a proven finding will not be merged. See [ADR 0001](docs/adr/0001-ai-proposes-engine-verifies.md).
2. **No false positives for recall.** A new oracle or probe must not report anything on the demo apps with every
   defect switched off. Run the benchmark's false-positive pass before and after.
3. **Numbers come from the harness.** Claims in the README come from `docs/BENCHMARKS.md`, which is generated. Do not
   edit numbers by hand.
4. **Secrets never reach a model or a log.** Anything that renders page text, network bodies or screenshots must go
   through the existing masking.

## Development setup

Requirements: .NET 9 SDK, Node 20+.

```bash
dotnet build BugHunter.sln
npm --prefix src/browser-worker install
npm --prefix src/web install
```

Run the apps as described in the [README](README.md#quick-start). Running apps lock their build output; to build or test
while they run, add `--artifacts-path <some temp dir>`.

## Before opening a pull request

```bash
dotnet test tests/BugHunter.Tests                 # unit and API tests
npm --prefix src/web run lint
npx --prefix src/web tsc --noEmit -p src/web
node scripts/smoke-hunt.mjs                       # with the demo shop and API running: no AI, all defects on
```

For changes to exploration, oracles, rules or prompts, also run the relevant benchmark rows and include the result
lines in the pull request:

```bash
node scripts/benchmark.mjs --only heuristic-80
node scripts/benchmark.mjs --app hotel --model provider:model --only guided-60-ai --defects all-on
```

CI runs the tests and a real smoke hunt on every push.

## Design decisions

Significant decisions are recorded as ADRs in `docs/adr/`. If your change reverses or extends one, add a new ADR (copy
the format of an existing one) in the same pull request. [extending.md](docs/extending.md) covers the common extension
points: rules, skill packs, oracles, routines and model providers.

## Style

- C#: follow the surrounding code. Small static functions over class hierarchies; comments say why, not what.
- TypeScript: the UI uses the components in `src/web/src/components/ui.tsx`; keep to its white, quiet visual language.
- Commit messages: `type(scope): what changed` (for example `feat(oracles): refunds above the order total`).

## Reporting bugs in Bug Hunter

Open an issue with the hunt ID, the strategy and models, and the Markdown report (`Export` on the hunt page). For a
false positive, include the finding's recorded evidence: that is exactly what the fix will be tested against.
