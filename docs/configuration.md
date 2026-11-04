# Configuration

Every setting is standard ASP.NET Core configuration: `src/BugHunter.Api/appsettings.json`, then
`appsettings.{Environment}.json`, then environment variables (`Section__Key`, for example `Models__Default`), then
command-line arguments. Secrets belong in environment variables or a secret store, never in `appsettings.json`.

## Models

| Key | Default | Meaning |
|---|---|---|
| `Models:Default` | set in `appsettings.json` | Driver model, as `provider:model`: many short calls (the next action on a page) |
| `Models:Planner` | set in `appsettings.json` | Planner model: a few calls (app model, intents, rules, probe ideas). Empty means the driver plans too |
| `Models:MaxCostUsd` | `1.0` | Default AI budget per hunt; past it the hunt finishes without AI |
| `Models:OllamaUrl` | `http://localhost:11434` | Local Ollama server, for `ollama:<model>` selections |
| `Models:OllamaContextTokens` | `16384` | Context window requested from Ollama; its own default is far smaller and silently drops the rest of a long prompt |
| `Models:AnthropicApiKey` | `ANTHROPIC_API_KEY` | Anthropic |
| `Models:GeminiApiKey` | `GEMINI_API_KEY` | Google (AI Studio key, or Google application default credentials) |
| `Models:OpenAiApiKey` | `OPENAI_API_KEY` | OpenAI-compatible |

Bring your own key: nothing in the repository holds one. A selection is `provider:model`, where provider is
`anthropic`, `gemini`, `openai` or `ollama` and model is any model that provider serves. Each hunt can override both
models and the budget. Keys can also be set for a running instance from **Settings** (kept in memory only). `POST
/api/hunter/models/check` (or **Settings** in the UI) makes one small call to prove a selection works.

## Hunter

| Key | Default | Meaning |
|---|---|---|
| `Hunter:WorkerUrl` | `http://127.0.0.1:5299` | The browser worker |
| `Hunter:WorkerToken` | none | Shared bearer token between API and worker (`WORKER_TOKEN` on the worker). Set it whenever the worker is not on localhost |
| `Hunter:AutoStartWorker` | on outside Production | The API starts `src/browser-worker` itself when it is not running |
| `Hunter:WorkerDir` | found above the API | Where the worker's code is, for auto-start |
| `Hunter:MaxConcurrentHunts` | `2` | Hunts running at once; more are queued |
| `Hunter:MaxMinutes` | `20` | Wall-clock limit per hunt, replays included |
| `Hunter:AllowPrivateTargets` | on outside Production | Allow hunting `localhost` and private-network hosts |
| `Hunter:MediaDir` | `<data>/media` | Recordings |
| `Hunter:SkillsDir` | the `skills` folder above the API | Skill packs ([extending.md](extending.md)) |
| `Hunter:DemoShopUrl`, `Hunter:DemoHotelUrl` | `http://localhost:5290/`, `:5291/` | Demo apps, for benchmarks only |
| `Hunter:DemoShopKey` | `DEMO_SHOP_KEY` | The demo apps' admin key (defect switches, test reset) |

## Projects and simulators

| Key | Default | Meaning |
|---|---|---|
| `Simulators:Url` | `http://localhost:5292` | The Jira and Confluence simulators; the demo projects sync from them. Empty: the demo projects read the knowledge files instead |
| `Simulators:Email`, `Simulators:ApiToken` | `bughunter@example.test`, `SIM_TOKEN` | The account the demo projects use (Atlassian Cloud authenticates with an email and an API token). No token is committed: it comes from `SIM_TOKEN`, generated into `.env` by `scripts/dev.mjs` |

A project's API specifications are part of the project too (`specs`: `{ "kind": "openapi", "url": ... }` or a `path`);
`POST /api/projects/{id}/specs/import` imports them into the twin, and a HAR file sent as the request body is imported
as recorded traffic.

A project's knowledge sources are part of the project (`POST /api/projects`): `files` (a `stories.json` file and a
folder of Markdown pages), `jira` (base URL, project key, email, and the project secret holding the API token) and
`confluence` (base URL, space key, email, token secret). Project secrets are set with `PUT /api/projects/{id}/secrets`
and are never returned.

The simulators app (`src/Simulators`) reads `Simulators:ApiToken` (API calls) and `Simulators:HarnessKey` (`/sim/reset`),
and seeds itself from `ground-truth/<app>/knowledge/` (`Simulators:GroundTruthDir` to point elsewhere).

## Storage

| Key | Default | Meaning |
|---|---|---|
| `Storage:Provider` | `File` | `File` (JSON documents on disk, one node) or `Postgres` (JSONB, several API instances) |
| `Storage:DataDir` | `data/` at the repository root | File store location |
| `ConnectionStrings:BugHunter` | none | PostgreSQL connection string; the table is created on start |

## Security and limits

| Key | Default | Meaning |
|---|---|---|
| `Auth:ApiKey` | `BUGHUNTER_API_KEY` | When set, every `/api` call needs `X-Api-Key` or `Authorization: Bearer`. Health and docs stay open |
| `RateLimits:HuntsPerMinute` | `10` | Hunts that can be started per minute (HTTP 429 beyond) |
| `Cors:Origins` | `["http://localhost:3100"]` | Origins allowed to call the API from a browser |

## Integrations

| Key | Default | Meaning |
|---|---|---|
| `GitHub:Token` | `GITHUB_TOKEN` | Token with `issues: write` on the repository |
| `GitHub:Repo` | none | `owner/name`; enables **File GitHub issue** on findings |
| `GitHub:ApiUrl` | `https://api.github.com` | GitHub Enterprise Server: `https://host/api/v3` |
| `OTEL_EXPORTER_OTLP_ENDPOINT` | none | Export traces and metrics over OTLP (Jaeger, Tempo, Honeycomb, Azure Monitor, ...) |

## Browser worker (environment variables)

| Variable | Default | Meaning |
|---|---|---|
| `PORT`, `HOST` | `5299`, `127.0.0.1` | Listen address |
| `WORKER_TOKEN` | none | Required bearer token (match `Hunter:WorkerToken`) |
| `MAX_SESSIONS` | `4` | Concurrent browser sessions |
| `SESSION_IDLE_MS` | `600000` | Idle sessions are closed after this |
| `VIDEO_DIR` | OS temp | Where recordings are written before the API collects them |
| `HEADED` | off | `1` shows the browser window (debugging) |

## Web UI

| Variable | Default | Meaning |
|---|---|---|
| `NEXT_PUBLIC_API_URL` | `http://localhost:5280` | The API, baked in at build time |

When the API requires a key, paste it in **Settings**; it is kept in the browser's local storage only.
