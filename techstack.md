# Tech Stack — Grocery ERP

> Desktop ERP for a single grocery store: offline, single user, one Windows PC.
> Status: **decided** · Last reviewed: September 2026
> Companion documents: `implementation.md`, `PLANS.md`, `grocery_erp_schema.sql`,
> `002_i18n_pricing_reporting.sql`, `003_operations.sql`

---

## 1. Decision summary

The application is a **native Windows desktop app built with WPF on .NET 10 (LTS)**, using a
Fluent (Windows 11-style) theme, **SQLite** as an embedded single-file database accessed through
**Dapper**, and a strict fixed-point number model (3 decimal places everywhere). It ships as a
self-contained installer that needs no internet, no runtime installation and no database server.

| Layer | Choice |
|---|---|
| Runtime | .NET 10 LTS, C# 14, `net10.0-windows`, x64, self-contained |
| UI framework | WPF |
| Look & feel | WPF UI 4.x (Fluent Design: light/dark, Mica, NavigationView, Snackbar, ContentDialog) |
| MVVM | CommunityToolkit.Mvvm 8.x (source generators, messenger) |
| Composition | Microsoft.Extensions.Hosting (DI, configuration, logging) |
| Database | SQLite 3.4x (bundled `e_sqlite3`) via Microsoft.Data.Sqlite |
| Data access | Dapper 2.x + hand-written SQL |
| Schema migrations | In-house runner, embedded `.sql` scripts, `PRAGMA user_version` |
| Numbers | C# `decimal` in code, `INTEGER × 1000` in the database |
| Search | SQLite FTS5 with the `trigram` tokenizer |
| Barcodes | Own EAN/GS1 code + ZXing.Net for on-screen/A4 rendering |
| Receipts & labels | Raw ESC/POS and TSPL over the Windows spooler (winspool) |
| A4 documents | WPF `FixedDocument` → any printer or "Microsoft Print to PDF" |
| Excel import/export | ClosedXML |
| Charts | LiveCharts2 2.0.x (WPF) |
| Localization | `.resx` resources + runtime-switchable markup extension; English (default) and Gujarati; numbers always English digits |
| Password hashing | Argon2id (Konscious.Security.Cryptography.Argon2) |
| Logging | Serilog → rolling files |
| Tests | xUnit v3, Shouldly, Verify, FsCheck, FlaUI, BenchmarkDotNet, Bogus |
| Backups | `VACUUM INTO` snapshot → `.zip` (`System.IO.Compression`) with a SHA-256 manifest; verified after every write and before every restore |
| Duplicate protection | Operation IDs claimed in the database (migration 003) |
| Installer & updates | Velopack (updates from a USB stick / local folder) |

---

## 2. Constraints that drive every choice

| Constraint | Consequence |
|---|---|
| **Always offline** | No cloud services, no online licensing, no CDN assets. Installer must be self-contained. Updates come from a folder/USB. Backups are the app's responsibility. |
| **Single user, single PC** | Embedded database, no server, no roles/permissions, no concurrency control beyond one writer. |
| **Windows only** | Use native Windows UI and printing; cross-platform frameworks buy nothing. |
| **3-decimal precision everywhere** | No binary floating point for money, quantity or percent — anywhere. |
| **Fast search & scanning** | In-process DB with proper indexes, full-text search, keyboard-first UI, virtualized grids. |
| **Highly modular** | One project per business module, explicit contracts between modules, append-only extensibility points in the schema. |
| **Bilingual (English default, Gujarati)** | Runtime-switchable UI language, Gujarati-capable fonts, bilingual product names, raster printing for Gujarati. Numbers stay in English digits in both languages. |
| **Modest shop hardware** | Low RAM use, fast cold start, no heavy effects required, works at 1366×768. |
| **Unattended reliability** | Power cuts, no IT admin: durable writes, automatic backups, self-checks at startup, clear recovery paths. |

---

## 3. Runtime & language

**.NET 10 (LTS)** — released November 2025, supported until November 2028. LTS releases are the
only ones this project targets. Plan the upgrade to the next LTS (.NET 12, expected November 2027)
during 2028; the upgrade is normally a target-framework bump plus package updates.

- **C# 14**, nullable reference types on, warnings as errors.
- **Target:** `net10.0-windows` (the domain library `GroceryErp.Core` targets plain `net10.0` so it
  is testable anywhere and cannot accidentally depend on WPF).
- **Deployment:** self-contained `win-x64` with **ReadyToRun** compilation for fast cold start.
  WPF does not support trimming or Native AOT, so neither is used.
- **Globalization:** ICU mode (the default on Windows 10 1903+ / Windows 11) — required for
  correct `gu-IN` culture data (translations, month names). `InvariantGlobalization` stays **off**.

**Supported OS:** Windows 11 (recommended for new PCs). Windows 10 22H2 left mainstream support on
14 October 2025 — before deploying to a Windows 10 shop PC, confirm it is on .NET 10's
supported-OS list for the exact build.

---

## 4. User interface

### 4.1 WPF

Chosen over the alternatives because it is the most mature Windows desktop UI stack, has a
built-in high-performance virtualized `DataGrid`, first-class printing (`PrintDialog`,
`FixedDocument`, `RenderTargetBitmap`), full IME/complex-script text support for Gujarati,
control over digit rendering (`NumberSubstitution`), and a large body of documentation and answered questions.

### 4.2 WPF UI 4.x (Fluent theme)

Stock WPF controls look dated, and Microsoft's built-in Fluent theme for WPF is still being
completed in .NET 10. **WPF UI** (`WPF-UI` on NuGet, MIT) provides:

- Fluent styles for all standard controls, light/dark/high-contrast themes, accent colors.
- `FluentWindow`, `NavigationView`, `ContentDialog`, `Snackbar`, `NumberBox`, `InfoBar`, `SymbolIcon`
  (Fluent System Icons).
- Optional Mica/Acrylic backdrops — **disabled by default** on low-end PCs (setting `ui.backdrop`).

### 4.3 MVVM & composition

- **CommunityToolkit.Mvvm 8.x** — `[ObservableProperty]`, `[RelayCommand]` (async, cancellable),
  `ObservableValidator`, `WeakReferenceMessenger` for cross-module notifications.
- **Microsoft.Extensions.Hosting** — generic host for DI, logging and configuration, used only by
  the App. Modules register their own services through `IModule.Register`, which needs just
  `Microsoft.Extensions.DependencyInjection.Abstractions`; the domain library `GroceryErp.Core`
  has no DI (or any other package) dependency.

### 4.4 Charts

**LiveCharts2** (`LiveChartsCore.SkiaSharpView.WPF`, 2.0.x stable) for the dashboard and report
charts. Fallback if it ever becomes a problem: **ScottPlot 5** (`ScottPlot.WPF`).

### 4.5 Fonts

- UI font stack: **Segoe UI, Nirmala UI**. Nirmala UI ships with Windows 10/11 and covers Gujarati;
  WPF falls back to it automatically for Gujarati characters.
- Digits are pinned to English (`NumberSubstitution.Substitution="European"`) on every window and
  print visual, so Windows regional settings can never turn `123` into `૧૨૩`.
- Receipts are rendered with the same fonts into a bitmap (see §8), so no printer font support
  for Gujarati is needed.

---

## 5. Data

### 5.1 SQLite (embedded)

- Accessed through **Microsoft.Data.Sqlite**, which bundles its own SQLite build (`e_sqlite3`
  via SQLitePCLRaw). No system SQLite, no server, no installation.
- Required features: **STRICT tables (≥ 3.37)**, **FTS5 with trigram tokenizer (≥ 3.34)**,
  `RETURNING` (≥ 3.35), `VACUUM INTO`. The app verifies these at startup and refuses to run if
  any is missing (see implementation §3.13).
- Mode: **WAL** journal, `synchronous = FULL` (power-cut safe), `foreign_keys = ON` on every
  connection.
- One database file: `%LocalAppData%\GroceryErpData\db\grocery.db`.

**Why not the alternatives?**

| Option | Reason rejected |
|---|---|
| SQL Server Express / LocalDB | Service/instance management, bigger install, overkill for one user. |
| PostgreSQL (embedded) | Server process to manage on a shop PC; no benefit for one user. |
| LiteDB / other document DBs | Weak for relational reporting (P&L, ledgers, joins); no SQL views. |
| Plain files / JSON | No transactions, no integrity constraints, slow reporting. |

### 5.2 Dapper + hand-written SQL

- The schema relies on **triggers, views, partial indexes, composite foreign keys and FTS5** —
  features an ORM would hide or fight. SQL stays explicit and reviewable.
- **Dapper 2.x** maps results to DTOs; custom type handlers map `Money`/`Quantity`/`Percent`
  (`INTEGER × 1000`) and `DateOnly` (`TEXT 'YYYY-MM-DD'`).
- **EF Core is deliberately not used.** Its SQLite provider stores `decimal` as TEXT (breaking
  `SUM`, ordering and indexes) and its migrations don't manage triggers/FTS well.

**Important:** Microsoft.Data.Sqlite's async methods execute synchronously. All DB work is pushed
to the thread pool by a `DbExecutor` that also serializes writes (implementation §3.3).

### 5.3 Migrations

In-house runner (~100 lines): numbered `.sql` files embedded in `GroceryErp.Data`, applied in
order inside a transaction, version stored in `PRAGMA user_version`, automatic backup before any
migration, and a hard stop if the database is newer than the app.

---

## 6. Numbers & money

| Where | Representation |
|---|---|
| Database | `INTEGER` scaled by 1000 (`₹12.345` → `12345`, `1.250 kg` → `1250`, `18%` → `18000`) |
| C# domain | `readonly record struct` types `Money`, `Quantity`, `Percent` wrapping `decimal`, always rounded to 3 places |
| Rounding | Half away from zero (`MidpointRounding.AwayFromZero`), applied once per operation |
| UI | Formatting only — no arithmetic in views or converters |
| Banned | `double` / `float` for any business value (enforced by analyzers in `GroceryErp.Core`) |

---

## 7. Search

- **SQLite FTS5, `trigram` tokenizer** over SKU, name, local-language name, aliases and brand —
  substring matching in English and Gujarati; Gujarati digits are normalized to `0–9` first.
- Barcode lookups hit a **unique index** (single B-tree seek).
- 1–2 character queries fall back to an indexed prefix `LIKE` on the product name.
- Results re-ranked in memory by recent sales popularity.

---

## 8. Barcodes, printing & hardware

### 8.1 Software

| Concern | Choice |
|---|---|
| EAN-8/13, UPC-A validation & check digits | Own code (~40 lines) |
| Internal barcodes | EAN-13 in the GS1 in-store range 20–29 |
| Weighing-scale labels | Configurable rules table (`barcode_rule`) |
| GS1 2D codes (DataMatrix/GS1-128) | Own parser for AIs (01) GTIN, (10) batch, (17) expiry, (21) serial |
| On-screen / A4 barcode images | ZXing.Net (`BarcodeWriterPixelData` → `WriteableBitmap`) |
| Receipt printing | Raw **ESC/POS** bytes via winspool; Gujarati text as raster image (`GS v 0`) |
| Label printing | Raw **TSPL** (TSC/TVS and compatibles) via winspool; Gujarati text via `BITMAP` |
| Cash drawer | ESC/POS kick pulse through the receipt printer |
| A4 invoices & reports | WPF `FixedDocument` → `PrintDialog`; PDF via "Microsoft Print to PDF" |
| Excel | ClosedXML (import templates, report export) |

### 8.2 Recommended hardware

| Device | Recommendation | Why |
|---|---|---|
| PC | 4-core CPU, **8 GB RAM**, **SSD**, 1366×768 or better, Windows 11 | SSD matters more than CPU for SQLite |
| Barcode scanner | **2D imager**, USB HID (keyboard) mode, suffix = Enter | Reads crumpled packs, phone screens, GS1 DataMatrix |
| Receipt printer | 80 mm thermal, ESC/POS, 203 dpi, USB, auto-cutter | 576-dot raster width, fast raster printing |
| Label printer | Thermal, TSPL, 203 dpi, 50×25 mm labels | Price/barcode labels, lot labels |
| Cash drawer | RJ11/RJ12 to the receipt printer | Opened by ESC/POS pulse |
| Weighing scale | Label-printing scale with configurable EAN-13 format | Loose items via variable-measure barcodes |
| UPS | 600–1000 VA line-interactive | Power cuts are the #1 risk to an offline POS |
| Backup media | Two USB drives, rotated | Off-machine copy of the database |

---

## 9. Localization (i18n)

| Concern | Choice |
|---|---|
| String resources | `.resx` per module: `Strings.resx` (English, neutral) + `Strings.gu.resx` (Gujarati) |
| Runtime switching | `TranslationSource` singleton + `{l:Tr Key}` markup extension — no restart |
| Editing translations | **ResXManager** (Visual Studio extension / standalone) with Excel export for the translator |
| UI languages | **English (`en-IN`) — default**; Gujarati (`gu-IN`) — optional, switchable any time |
| Numbers | **Always English digits `0–9`** in both languages — screen, input, receipts, labels, reports, exports. Fixed `en-IN` formatting (lakh grouping `1,23,45,678.000`), not a setting |
| Digit rendering | WPF `NumberSubstitution` pinned to European digits on windows and print visuals |
| Product data | Bilingual names (`name` English + `name_local` Gujarati), Latin aliases for search |
| Keyboard input | Windows Gujarati Phonetic keyboard for Gujarati names; Gujarati digits converted to `0–9` in every field; scan fields forced to English layout |
| Printing | Bill language (English default / Gujarati) + bilingual names; Gujarati rendered as raster |
| QA | Pseudo-localization mode, missing-key, placeholder and "no Gujarati digits" tests |

Details: implementation §5.

---

## 10. Security

- **Single local user**, password hashed with **Argon2id** (Konscious.Security.Cryptography.Argon2).
- **No password recovery** (by requirement). Therefore the **database is not encrypted**: the
  login is a screen lock, not data protection — an encrypted DB with a forgotten password would
  mean permanent loss of the store's records. For theft protection, use **BitLocker** on the drive
  (Windows Pro/Enterprise).
- Idle auto-lock (default 15 minutes); in-progress bills are preserved.
- Single-instance guard (named mutex) so two app instances never write concurrently.
- Data folder deliberately **outside** OneDrive-synced folders (Documents/Desktop) — a sync client
  touching a live SQLite file can corrupt it.

---

## 11. Logging & diagnostics

- **Serilog** (`Serilog.Extensions.Hosting`, `Serilog.Sinks.File`): rolling daily files, 30-day
  retention, in `…\GroceryErpData\logs`.
- Global handlers for dispatcher, task and domain exceptions → log + friendly dialog.
- "Export diagnostics" button: zips logs, settings (no password hash) and DB statistics for support.

---

## 12. Testing & quality

| Tool | Use |
|---|---|
| **xUnit v3** | Unit and integration tests |
| **Shouldly** | Assertions (FluentAssertions v8+ is commercially licensed — avoided) |
| **FsCheck** | Property-based tests for money math, allocation, tax splits |
| **Verify** | Snapshot tests for receipt bytes, receipt images, report output |
| **FlaUI** | UI smoke tests (launch → login → create product → sell) |
| **BenchmarkDotNet** | Hot-path benchmarks (pricing, search, FEFO, P&L) |
| **Bogus** | Synthetic 5-year dataset generator |
| **NetArchTest** (or ArchUnitNET) | Module-boundary rules |
| Roslyn analyzers | `AnalysisLevel=latest-recommended`, `BannedApiAnalyzers` in Core |
| `.editorconfig` | Formatting and naming rules |

---

## 13. Build, packaging & updates

- **SDK-style projects**, one solution, `Directory.Build.props` for shared settings and
  **Central Package Management** (`Directory.Packages.props`) so every package version is pinned
  in one place.
- **Velopack** for installation and updates:
  - `vpk pack` produces `Setup.exe` plus full/delta packages.
  - Installs per-user without admin rights.
  - Updates are applied from a **local folder or USB stick** (`new UpdateManager(folderPath)`),
    after an automatic pre-update backup.
- Optional code signing (avoids the SmartScreen "unknown publisher" prompt on first install).
- Optional CI: GitHub Actions `windows-latest` runner for build + tests on every push (the dev
  machine is online even though the shop PC is not).

---

## 14. Package list

Pin the latest stable patch of each line in `Directory.Packages.props` when the package is first
introduced. Versions already introduced are exact pins reviewed on 22 September 2026; packages
for later phases remain unpinned until they are actually added.

| Package | Line | Project(s) | License |
|---|---|---|---|
| `WPF-UI` | **4.3.0** | App, UI.Common, modules | MIT |
| `CommunityToolkit.Mvvm` | **8.4.2** | App, UI.Common, modules | MIT |
| `Microsoft.Extensions.Hosting` | **10.0.12** | App | MIT |
| `Microsoft.Extensions.DependencyInjection.Abstractions` | **10.0.12** | UI.Common, modules | MIT |
| `Microsoft.Data.Sqlite` | 10.x | Data | MIT |
| `Dapper` | 2.x | Data, modules | Apache-2.0 |
| `LiveChartsCore.SkiaSharpView.WPF` | 2.0.x | Reports | MIT |
| `ZXing.Net` | 0.16.x | Printing, Catalog | Apache-2.0 |
| `ClosedXML` | 0.10x | Catalog (import), Reports (export) | MIT |
| `Konscious.Security.Cryptography.Argon2` | 1.x | Settings (auth) | MIT |
| `Serilog.Extensions.Hosting`, `Serilog.Sinks.File` | **10.0.0**, **7.0.0** | App | Apache-2.0 |
| `Velopack` | latest | App | MIT |
| `xunit.v3`, `Shouldly` | **4.0.1**, **4.3.0** | tests | Apache-2.0/BSD-3-Clause |
| `Microsoft.NET.Test.Sdk` | **18.10.1** | tests | MIT |
| `Microsoft.CodeAnalysis.BannedApiAnalyzers` | **5.6.0** | Core (build only) | MIT |
| `Verify.XunitV3`, `FsCheck` (+ its xUnit v3 integration) | latest when introduced | tests | MIT/BSD |
| `FlaUI.UIA3` | latest | UI tests | MIT |
| `BenchmarkDotNet`, `Bogus` | latest | tools | MIT |

Check each license again at pin time; licenses of popular .NET packages have changed before.

---

## 15. Rejected alternatives

| Option | Why not |
|---|---|
| **Tauri 2 / Electron + web UI** | Excellent visuals, but adds a second language/runtime (Rust or Node), WebView2 bundling for offline PCs, and weaker Windows printing/hardware integration. |
| **WinUI 3** | Newest Microsoft UI, but tooling, packaging and control maturity trail WPF; no benefit for this app. |
| **Avalonia** | Very good, WPF-like; its main advantage (cross-platform) is irrelevant here. Reasonable fallback. |
| **.NET MAUI** | Mobile-first; weak for dense desktop data-entry UIs. |
| **Java + JavaFX** | Workable (AtlantaFX theme, `BigDecimal`), but smaller desktop ecosystem and clunkier Windows printing/hardware. |
| **EF Core** | Decimal-as-TEXT on SQLite; fights triggers, views and FTS5. |
| **Encrypted DB (SQLCipher)** | With no password recovery, a forgotten password destroys the business records. |

---

## 16. Version & upgrade policy

- Runtime: LTS only. Move to the next LTS within 12 months of its release.
- Packages: review quarterly; patch updates freely, minor/major only with a full test run.
- Database: schema version = `PRAGMA user_version`; every change is a new numbered migration,
  never an edit to an old one.
- App versions: SemVer (`MAJOR.MINOR.PATCH`); the app refuses to open a database whose
  `user_version` is newer than it understands.

---

## 17. Development environment

- Visual Studio (a release that supports .NET 10) or JetBrains Rider.
- **ResXManager** extension for translations.
- **DB Browser for SQLite** or **SQLiteStudio** for inspecting databases (dev machine only).
- Git; conventional commits; one branch per module/phase.
- A test printer set (58/80 mm receipt printer, TSPL label printer) and a 2D scanner on the dev
  desk from Phase 1 — hardware quirks are cheaper to find early.
