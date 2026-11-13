# Project Brief — Loqzen POS

> Source of truth: `/README.md` + `/Docs/*.md` (Spanish specs). This memory bank summarizes them for the Architect/Orchestrator agent.

## What it is
Standalone, **Offline-First** multiplatform Point of Sale + local Backoffice + restaurant **KDS / mobile waiter (comanderas)** module.
- Targets: Windows, Linux, Android (Avalonia UI, .NET 8; docs say 8/9 — code is pinned to `net8.0`).
- 100% autonomous: own master data (products, categories, prices, taxes, customers). **No mandatory ERP/cloud dependency.**
- Optional future sync to ERP via REST/SignalR through the **Outbox Pattern**.
- Repo: `williamag929/loqzen-pos`. License: proprietary.

## Invariable rules (from Docs/pos-prompt-antigravity.md + Docs/README.md)
1. **Clean Architecture**: Presentation/Infrastructure depend inward only. Business logic lives in `Core.Domain` / `Core.Application`.
2. **Guid PKs** on every master & transactional entity (offline collision safety).
3. **Hardware only via interfaces** (`IPrinterService`, `IScaleService`, `IBarcodeScannerService`, `ICashDrawerService`). Views/ViewModels never touch hardware directly; real vs Docker-emulated must be swappable.
4. **Offline-First**: every transaction commits immediately to local SQLite; network loss never blocks sales.
5. **Outbox**: events to sync are written to `OutboxMessages` in the same DB transaction.
6. **DI**: use Mock/emulated implementations when environment = Development.
7. **Touch-first UI**: 15.6" / 18.5" screens, touch targets ≥ 48×48 px, strict MVVM (CommunityToolkit.Mvvm).
8. Iteration deliverable: `dotnet build` passes with no architecture violations + artifact report.

## Roadmap
- **Phase 1** Core POS: solution skeleton, SQLite/EF Core, masters, checkout UI, mixed payments, cash shift open/close.
- **Phase 2** Hardware: ESC/POS + ZPL printing, cash drawer pulse `0x1B 0x70`, CAS SW-20RS serial scale, USB HID scanner, Docker emulators.
- **Phase 3** Dual screen (`CustomerDisplayWindow` + payment QR), embedded Kestrel, KDS, comanderas, offline fallback, mDNS discovery.
- Extra: Delivery bridge (Rappi / Uber Eats webhooks) → auto kitchen ticket + stock decrement.

## Reference hardware (Docs/Hardware_POS_Seleccionado.md)
| Device | Model | Interface |
|---|---|---|
| Terminal | Volcora POS Standard Plus 18.5" (Intel N97) | USB, RS-232, LAN, Wi-Fi, 2nd display |
| Receipt printer | Epson TM-T20III / TM-T88VI | TCP 9100 / USB, ESC/POS, DKD port |
| Cash drawer | Volcora 16" | RJ11/RJ12 to printer, pulse `ESC p` (0x1B 0x70) |
| Scale | CAS SW-20RS | RS-232, frames like `ST,GS,+001.25kg\r\n` |
| Scanner | USB HID keyboard-wedge | global hook, no focus loss |
