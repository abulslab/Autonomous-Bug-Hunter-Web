# Browser worker

The Bug Hunter's browser. It runs real Chromium sessions with Playwright so the API process never runs a browser, and the API drives it over a small HTTP protocol.

In local development you don't need to start it: outside Production the API starts it when the API boots, and again when the Bug Hunter page loads or a hunt starts if it isn't running. On first use the API also runs `npm install` and installs Chromium, and it stops the worker when the API stops. Turn this off with `Hunter:AutoStartWorker=false`; it only applies when `Hunter:WorkerUrl` is on localhost. `Hunter:WorkerDir` overrides where the API looks for this folder.

To run it yourself:

```
cd src/browser-worker
npm install
npx playwright install chromium   # only if Chromium for Playwright 1.64 is not installed yet
npm start                         # http://127.0.0.1:5299
```

The API finds it through `Hunter:WorkerUrl` (default `http://127.0.0.1:5299`). Set `WORKER_TOKEN` here and `Hunter:WorkerToken` in the API to require a bearer token.

## Protocol

| Call | Purpose |
| --- | --- |
| `POST /sessions` `{ url, allowedHosts?, secrets? }` | Open a session and return the first snapshot |
| `POST /sessions/:id/act` `{ type, ref?, value?, url?, key?, snapshot? }` | `click`, `fill`, `select`, `check`, `press`, `navigate`, `back`, `reload`, `scroll` or `wait`, then a snapshot (skipped with `snapshot: false`) |
| `POST /sessions/:id/snapshot` | Snapshot without acting |
| `DELETE /sessions/:id` | Close the session |
| `GET /healthz` | Liveness and session count |

A snapshot contains the URL, title, headings, visible text, the interactive elements tagged with numeric refs (role, accessible name, label, value, form), a JPEG screenshot, and the console errors, network calls (with JSON bodies), dialogs and blocked requests recorded since the previous snapshot.

## Safety

- Every request to a host other than the target's (and any `allowedHosts`) is aborted, and the hosts it tried are reported.
- Secrets are passed to the session, never to the model. The model writes `{{secret:name}}`; the worker substitutes the value when it fills the field, and replaces the value with the reference in everything it returns (text, element values, network bodies, error messages). Password fields and fields filled from secrets are masked in screenshots.
- Sessions are capped (`MAX_SESSIONS`, default 4) and closed after `SESSION_IDLE_MS` of inactivity (default 10 minutes).

| Variable | Default |
| --- | --- |
| `PORT` / `HOST` | `5299` / `127.0.0.1` |
| `WORKER_TOKEN` | none |
| `MAX_SESSIONS` | `4` |
| `SESSION_IDLE_MS` | `600000` |
| `HEADED` | set to `1` to watch the browser |
