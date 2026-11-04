---
name: authentication
title: Sign-up, sign-in and sessions
match: sign in, log in, login, register, sign up, password, account, session, logout
---

## Risks
- Registration accepts malformed emails, duplicate accounts, or empty required fields.
- Sign-in error messages reveal whether an account exists.
- Calls that change data succeed without a session (missing authorization).
- After sign-out, the previous session token still works.
- One user can read or change another user's resources by changing an id in the URL or request (IDOR).

## Rules
- On registration writes: `status >= 400 || contains(request.email, "@")`
- On calls that change data, when sent without a session: the status is 401 or 403.

## Probe values
- Email: not-an-email, an existing account's email, 300 characters
- Password: empty, a single character
- Session: the request with no cookies or session headers
