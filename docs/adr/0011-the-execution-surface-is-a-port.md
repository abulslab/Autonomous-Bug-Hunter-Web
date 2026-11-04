# ADR 0011: The execution surface is a port

Status: Accepted

## Context

Bug Hunter is built and measured on web applications, but most of what makes it useful has nothing to do with a
browser: versioned knowledge from stories and documents, the app twin, AI understanding checked by a verifier, rules
and predictions evaluated on recorded traffic, model-free confirmation, runs and baselines, the review gate, learning
and release readiness. Native mobile apps and point-of-sale terminals have the same quality questions (does it do what
the stories say, did this release break anything, can we ship) with a different way of driving the application.

Copying the whole system for each kind of application would mean porting every fix to every copy, and the copies
would drift. Building abstractions for platforms that do not exist yet would slow down the one that does.

## Decision

The engine talks to the application only through the browser worker's contract, and that contract is treated as the
port for any execution surface:

| Operation | Today (web) | What another surface provides |
|---|---|---|
| Open a session (URL or entry point, allowed hosts, secrets) | `POST /sessions` on the browser worker | Launch the app on a device or emulator, with test accounts injected |
| Act (click, fill, select, navigate) on an element found by role and name | Playwright on the accessibility tree | UI automation on the platform's accessibility tree, or screen regions for a surface without one |
| Snapshot: elements, text, console, network events, screenshot | DOM and accessibility snapshot, captured `fetch` and `XMLHttpRequest` traffic | Accessibility tree, device logs, the app's HTTP traffic through a proxy, screenshot |
| Resend a recorded request with a change | Through the page's own session | Through the app's session (proxy or API client) |
| Replay steps in a fresh session after the reset hook | Fresh browser context | Fresh app install or cleared app data |

Everything above that line stays shared: oracles, rules and predictions read `NetworkEvent` and `PageSnapshot`, not a
browser; confirmation replays steps through the same operations; runs, review, learning and readiness never see the
surface at all.

The rule is applied now only as a boundary to respect, not as new code: nothing outside the worker client may depend
on browser specifics. The shared core is extracted into its own package when a second surface is actually built.

## Consequences

- A new kind of application needs a driver that implements the five operations, not a new Bug Hunter.
- What carries over unchanged is largest where the application talks to a backend over HTTP: the rules,
  predictions, probes and replay all work on that traffic, whatever draws the screen.
- A surface without an accessibility tree (a canvas or a terminal UI) has to find elements another way, for example
  by vision, and replay becomes less stable there; its limits need to be measured on their own ground truth.
- Some current oracles are web-only (JavaScript errors, page text checks) and stay with the web surface.

## Alternatives considered

- One repository per platform, each a full copy: simplest to start, but every fix is ported by hand and the copies
  diverge.
- A platform-neutral core now, before a second platform exists: guesses at an interface no second driver has tested.
