# ADR 0007: Storage is a document-store interface: files or PostgreSQL

Status: Accepted

## Context

Hunts, screenshots and app maps are independent JSON documents. A single developer needs zero setup; a team needs shared storage for several API instances.

## Decision

`IDocStore` (get, list, put, delete by area, kind and key) with two backends: one file per document (default) and PostgreSQL with one JSONB table (`Storage:Provider=Postgres`).

## Alternatives considered

- A relational schema: premature; the documents are read and written whole.
- A vector database: not needed for structured maps; may help later with duplicate detection against existing tickets.

## Evidence

The platform this project was extracted from broke a user's database with schema changes; documents avoid migrations entirely.
