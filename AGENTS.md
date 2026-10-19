# AGENTS.md

Instructions for AI agents (Codex, Claude) working on TechAvailFeed: a .NET ingest service and API, with an
Angular dashboard (`web/`), for a scheduled tech-availability feed.

## Code Review Rules

Codex's PR review and the pre-push review gate both follow this section. Rate every finding on this scale:

- **P0**: data loss, leaked secrets or location data, or a change that breaks ingest or the API for everyone.
  Never merge.
- **P1**: wrong behaviour on a real path (wrong availability, counts or export rows), a feed row that can be
  misparsed, or a broken project invariant (below).
- **P2**: an edge case that breaks (empty feed, missing optional column, day boundaries, error paths), a doc
  or comment promise the code doesn't keep, a test that can pass while the code is wrong, or CI/script
  behaviour that differs from its description.
- **P3**: a minor issue a strict reviewer might still flag. P3s are filed as follow-up issues, not fixed in
  the PR that found them.

Every finding needs a concrete failure scenario (input or state → wrong result). Don't flag style, naming,
or anything CI already checks (build, tests, `dotnet format`, the web lint/format/build).

### Project invariants

- The feed's columns are defined in `docs/feed-format.md`. The parser (`FeedParser`) and the feed query change
  together; columns added later are optional, so an older export still parses.
- Business rules live in `src/TechAvail.Core` (availability, outcomes, arrivals, jeopardy, diagnostics), never
  in SQL, controllers or the dashboard. The dashboard computes no numbers; the API does.
- Job coordinates are location data: anything served goes through `Coords.Public`/`Coords.PublicBlock`, and
  Excel exports never carry them. The feed carries no address text.
- Every run stays exactly reconstructable: `blocks` rows are written once and closed by snapshot, ingest
  writes only the diff, and an `empty`/`error` snapshot never replaces the last `ok` one in anything
  user-facing.
- Cached reads: cache by snapshot id only what a committed snapshot fixes. Anything that changes with ingest
  is keyed on the `FeedChanges` generation, and an ingest write readers see sends `NOTIFY feed_changed` in the
  same transaction.
- API timestamps are naive local wall-clock times: never convert them through a timezone.
- Never commit `.env` or any credential.
- Commit subjects and PR titles: one-line Conventional Commits (`feat:`, `fix:`, `test:`, `docs:`, `ci:`,
  `chore:`, `refactor:`), lower-case imperative.

### Known pitfalls

Classes of bug past reviews caught here are in [`docs/pitfalls.md`](docs/pitfalls.md); check every change
against them.
