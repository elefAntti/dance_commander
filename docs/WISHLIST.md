# Dance Commander — Wish list

Ideas without a place on the [roadmap](ROADMAP.md) yet. Being here is not a promise: each one is
weighed against [DESIGN.md](DESIGN.md) first, and what was declined on purpose is in its §1.4.

## Probably not

These cross a line from DESIGN.md, or cost more than they would be used. Here so they aren't
proposed as new.

- **Directory compare and sync.** It needs a comparison engine and wouldn't be built just to tick a
  box.
- **A multi-rename tool** with regex and counters, for the same reason.
- **Branch switching and other git actions:** that's a git client. They belong in `tools.json`.
- **Writing into archives:** zip only, if ever.
- **Elevating an operation** through a broker process. It is security-sensitive.
- **More providers** (FTP, S3, phones): only on real demand.
- **A quick view that follows the cursor:** it reads every file you pass, which is costly on a
  server.
