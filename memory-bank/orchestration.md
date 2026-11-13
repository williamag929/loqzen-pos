# Orchestration — Architect Role

I (Cline) act as **Architect / Orchestrator**. I own the plan, review architecture rules, and delegate to role-specialized sub-agents.

## Team roles (from README "Multi-Agent Team Orchestration")
| Agent | Owns | Paths |
|---|---|---|
| `@dotnet-backend-architect` | Domain, Clean Arch, EF Core/SQLite, Outbox, migrations, application services | `src/Core.*`, `src/Infrastructure.Data` |
| `@ui-ux-frontend-architect` | Avalonia XAML, MVVM ViewModels, touch layouts (≥48px), numpads, customer display | `src/Presentation.Desktop` |
| `@hardware-infrastructure-agent` | ESC/POS, ZPL, serial scale, HID scanner, cash drawer, Docker emulators | `src/Infrastructure.Hardware`, `docker/` |
| `@networking-kds-agent` | Kestrel, REST/WebSockets, mDNS, KDS protocol, comandas fallback | `src/Presentation.Server` |
| `@qa-test-agent` | xUnit tests (Domain/Services/Integration SQLite/Receipts/Ui), reports implementation bugs without fixing src | `tests/` |

## How a phase runs (used 2026-11-13 for users + manager auth)
1. Orchestrator writes the **contract** first (entities, enums, DTOs, `I*Service`) so agents can work in parallel.
2. Backend + UI agents run concurrently, path-scoped, each building only its own project. QA starts when backend reports.
3. Orchestrator reviews, fixes what QA flags, runs the full gate, updates docs/memory-bank, commits. Subagents never commit.

## Orchestrator checklist for every task
1. Read `memory-bank/*` first; update `activeContext.md` at the end.
2. Map task → owning agent(s); split into atomic, path-scoped subtasks with explicit interfaces.
3. Enforce rules: Guid PKs, interfaces for hardware, no business logic in Views/ViewModels, Outbox in same transaction, offline never blocks.
4. Gate: `dotnet build LoqzenPos.sln` must pass; run tests when present.
5. Report artifacts (files changed, build result, open risks).

## Conventions
- C# file-scoped namespaces matching project (`Core.Domain.Entities`, `Infrastructure.Hardware.Printers`, ...).
- Several types per file grouped by concern (e.g. `Sales.cs`, `PrinterServices.cs`).
- UI strings Spanish; code/comments English or Spanish mixed (prefer English in code).
- Commit style: Conventional Commits (`feat:`, `docs:`).
