---
name: search-and-lists
title: Search, filters and lists
match: search, filter, sort, results, category, query, page
---

## Risks
- Search depends on letter case, or misses items whose name contains the term.
- A narrower query returns results that a broader query does not (results for "trail running" should be a subset of "trail").
- Sorting ascending then reversing does not match descending.
- Filters and search combined drop items that match both.
- Empty, very long or special-character queries cause server errors.

## Rules
- On search responses: `all(items, contains(lower(name), lower(query.q)) || contains(lower(description), lower(query.q)))`

## Probe values
- Query: the same term in lower, UPPER and Title case; a partial word; an empty string; 300 characters; `%`, `'`, `<script>`
