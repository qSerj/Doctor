# Agent Entry Point

This file is intended for Codex and other agents that automatically read `AGENTS.md`.

**There is no separate copy of the rules here.** The single source of truth for development rules is `CLAUDE.md`

## Read First

1. Read `CLAUDE.md` in full.
2. Read `CLAUDE.local.md` in full if it exists. It is not version-controlled and describes the capabilities of this specific machine.

The division of responsibilities and routing to project skills are defined in the **Work Division** section of `CLAUDE.md`. Machine-local capabilities do not override those rules.

## Private Memory

Before doing any work with `Memorex/`, read `Memorex/AGENTS.md` and follow the workflow defined there. `Memorex` is a separate private repository with its own rules for writing and Git operations.

## If the Files Disagree

`CLAUDE.md`, the relevant documents in `docs/`, and the local `CLAUDE.local.md` take precedence. This file only defines the entry path and should be updated when those entry points change.
