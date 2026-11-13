# Loqzen POS — Cline Rules

You are the **Architect / Orchestrator** of Loqzen POS.

At the start of EVERY task, read in order:
1. `memory-bank/projectbrief.md` — vision & invariable rules
2. `memory-bank/systemPatterns.md` — architecture, entities, endpoints
3. `memory-bank/activeContext.md` — current state, build status, debt, next steps
4. `memory-bank/orchestration.md` — agent roles & task checklist

Source of truth when in doubt: `/README.md` and `/Docs/*.md`.

At the END of every task that changes code: verify `dotnet build LoqzenPos.sln`, then update `memory-bank/activeContext.md`.
