# Security and threat model

Bug Hunter drives a real browser against applications, holds test credentials for the length of a hunt, and calls
external model providers. This page lists what it protects, the threats considered, and the controls in place.

## Assets

| Asset | Where it lives |
|---|---|
| Test credentials (secrets) | API process memory and the browser worker, for one hunt only; never stored |
| Model API keys | API configuration or environment variables; never sent to the browser or the UI |
| Findings, screenshots, recordings | Document store and the media folder |
| The application under test | Reachable only through the browser worker and the setup hook |

## Threats and controls

| Threat | Control | Where |
|---|---|---|
| A secret reaches the model or a report | The model only ever sees `{{secret:name}}`; the worker substitutes values and masks them in page text, network evidence, screenshots (Playwright masks), the live stream and recordings (overlay set before typing); specs read secrets from environment variables | `browser-worker/server.mjs`, `SpecWriter.cs` |
| The hunter wanders to other sites | Per-session host allowlist in the browser; every other request is aborted and recorded | worker `hostAllowed` |
| Server-side request forgery through a target or endpoint URL | URLs are checked against private and reserved ranges unless `Hunter:AllowPrivateTargets` (off in Production) | `Common/UrlGuard.cs` |
| The setup hook is aimed at a third party | It must be on the target's own host; its token is one of the hunt's secrets | `HunterEndpoints.cs` |
| Destructive actions (pay, place order, delete) on a real system | Blocked unless the target is a local or test host or the hunt explicitly allows them; probes skip order endpoints unless allowed and never send an order twice | `HuntEngine.cs`, `Probes.cs` |
| The model ends the session or misreports | AI-chosen steps cannot sign out unless the mission is about sessions; suspicions about the hunter's own tools are discarded; only engine-verified findings count as proven | `HuntEngine.cs` |
| Prompt injection from page content | Page text reaches the model only as data for choosing actions; the model cannot declare a bug proven, change the allowlist, read secrets or call anything but the worker's fixed action set | ADR 0001, ADR 0006 |
| Runaway model spend | Per-hunt AI budget enforced on every call; past it the hunt finishes without AI; cost reported per hunt and per confirmed bug | `ModelHuntPolicy.CallAsync` |
| Abuse of the API | Optional API key (`Auth:ApiKey`); fixed-window rate limit on starting hunts and replays; media served only through 30-minute tickets | `Program.cs` |
| Testing systems you do not own | Out of scope for the code; see below | |
| Provider keys set by someone else | Without an API key, `POST /api/hunter/models/config` accepts keys only from this machine | `HunterEndpoints.cs` |
| The demo apps' admin key leaks | It is held by the API and filled in server-side for hunts on the built-in demo apps; no response returns it | `HunterEndpoints.cs` |
| The stack is reachable from the network | Docker Compose publishes every port on `127.0.0.1` only | `docker-compose.yml` |

## Running it beyond your own machine

The defaults are for local development. Before the API is reachable by anyone else:

- Set `BUGHUNTER_API_KEY`; without it every `/api` call is open.
- Use fresh local keys: `DEMO_SHOP_KEY` (the demo apps' admin and reset key, also the simulators' harness key) and
  `SIM_TOKEN` (the simulators' API token) have no defaults in the repository; `scripts/dev.mjs` generates random
  values into a git-ignored `.env`. Set `POSTGRES_PASSWORD` yourself when you use the PostgreSQL store.
- Leave `Hunter:AllowPrivateTargets` off (it is on outside Production and in Docker Compose, so the demo apps on the
  local network can be tested).
- With no Gemini key configured, the API uses Google application default credentials if the machine has them (for
  example after `gcloud auth application-default login`), and calls are billed to that Google Cloud project.

## Authorization to test

Bug Hunter is for applications you own or are permitted to test. It sends boundary values, invalid input, repeated
submissions and unauthenticated requests, which an operator could mistake for an attack. Use test environments, test
accounts and test payment methods. Verifying ownership of a target (for example a DNS record) is on the roadmap for
multi-user deployments.

## Reporting a vulnerability

Please open a private security advisory on the GitHub repository rather than a public issue.
