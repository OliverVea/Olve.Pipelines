---
name: ovea-writing-self-igniting-letters
description: Write a self-igniting letter — an ephemeral handoff document that briefs a fresh agent on in-flight work and deletes itself on read. Use when context must survive a session boundary but should not become permanent documentation.
allowed-tools: Write Read Edit
---

# Writing Self-Igniting Letters

A **self-igniting letter** is an ephemeral handoff document. It exists to bridge a gap — between you and a future agent (or future you) who picks up mid-task with no conversation history — and is deleted immediately after being read. It is NOT documentation.

## When to write one

Write a letter when ALL of these apply:

1. **The work is mid-flight** and cannot be completed in the current session.
2. **A fresh agent couldn't pick up from git state alone.** The in-flight knowledge isn't in code or commits yet — background tasks, failed attempts, the reason behind a non-obvious choice, the exact state of a running build.
3. **The knowledge is temporary.** Once the task is done, this information is worthless and would rot. If it's useful long-term, write it into `CLAUDE.md`, a README, or memory instead.

If the context is already in git history, in comments, in memory files, or could be re-derived in under a minute — do NOT write a letter.

## When NOT to write one

- The task is finished. Write a PR description or commit message instead.
- The information is generally useful (architecture, conventions, gotchas). That belongs in `CLAUDE.md` or the auto-memory system.
- The session isn't actually ending — just keep working.
- The user hasn't asked for one and the next step is obvious from the code.

## What to include

Prefer **specifics over generalities**. The reader has nothing — no conversation context, no sense of what you tried, no idea why this matters. Give them enough to act, and nothing more.

**Include:**
- **Delete-on-read instruction, up top, before anything else.** First command in the letter should be `rm HANDOFF.md`.
- **What the user is trying to accomplish** (one sentence).
- **Current state** — what's running right now. Background task IDs, monitor IDs, PIDs, in-flight builds, open PRs, anything actively happening that the reader should be aware of.
- **Verify-this-first block** — a specific command or two the reader runs to orient themselves. Not "check the status" — the exact command.
- **What's already done** — short list of commits/changes that are already pushed, with short reasoning for each. Reference commit SHAs when useful.
- **Known quirks and gotchas** you discovered the hard way — failure modes, tight couplings, surprising behavior. The reader shouldn't have to rediscover these.
- **If it fails, here's where to look** — specific log commands, specific files.
- **Do NOT list** — actions the reader might be tempted to take that would cause harm (destructive git ops, restarting services with hidden dependencies, deleting state the reader doesn't recognize).

**Exclude:**
- Architecture explanations that belong in `CLAUDE.md`.
- Commands that are trivially re-derivable (`git status`, `dotnet build`).
- Chronological narrative of what you did. The reader doesn't care about the journey — they care about the current state and next action.
- Hedging language ("I think", "maybe", "probably"). If you don't know, say "unknown."

## Where to write it

Default to `HANDOFF.md` at the repo root. It's conventionally recognized and easy to spot. Do NOT nest it under `docs/`, `.claude/`, or any directory that might get ignored or mistaken for permanent content.

If there's already a `HANDOFF.md` from a previous session, overwrite it — don't append. An old letter is stale and misleading.

## Format

```markdown
# Self-Igniting Letter — <one-line task name>

**Read this once, then delete it immediately (`rm HANDOFF.md`) before doing anything else.** This file is ephemeral context, not documentation — it should not outlive the read.

## What the user is trying to do

<one sentence>

## Current state

<what's running RIGHT NOW — background tasks, builds, monitors, PIDs>

## Verify this first

```bash
<specific command(s) to orient the reader>
```

<what a "good" result looks like>

## What's been done

- commit `abc1234` — <why>
- commit `def5678` — <why>

## Known quirks / gotchas

1. <specific failure mode + why>
2. <tight coupling the reader wouldn't expect>

## If the deploy fails

```bash
<exact log-fetching commands>
```

## Do NOT

- <destructive action the reader might try>
- <service restart with hidden dependency>
```

## The golden rule

**The letter should make itself obsolete.** If the reader follows it and it contains anything they don't end up using, you wrote too much. Next time, write less.

If the reader finishes the task and wants to preserve something from the letter for next time, that something belongs in memory, `CLAUDE.md`, or a proper doc — NOT in a new HANDOFF.md. The letter ignites, the useful bits move to permanent homes.
