# Work we can do on this device

Date: 8 October 2026. This is a work plan, not release approval. The current machine is Windows 11 Home with about 16 GB RAM and 67 GB free on C: at the time of this inventory. There is no printer or scanner attached. Use isolated test data, never the operational store database.

| Order | Work | Done when | Evidence |
|---:|---|---|---|
| 1 | Close the remaining software gaps: move presentation/resources into module projects with architecture tests; localize unknown report codes and free-form audit details; add focused failure-path UI checks; complete public Core/Data XML documentation. | Relevant architecture, localization and UI tests pass; the current recheck in `luna/luna-gaps.md` is updated. | Code, tests, audit update |
| 2 | Build a fresh Release package and run the packaged operator journey, including setup, purchase, sale, Gujarati switch, lock/unlock and backup. | Installer/portable package builds and the currently skipped packaged UI test passes. | Package output and test log |
| 3 | Run the five-year synthetic data generator and performance suite on this PC; record disk use and baseline timings. | Results meet the documented targets and are recorded with machine, seed, scale and app revision. | `docs/perf/<version>.md` |
| 4 | Exercise data recovery with an isolated database: backup/restore, interrupted draft and print-job states, migration after update, integrity checks and rollback. Simulate interruption by terminating the test process at controlled points. | Data and journal state match the expected recovery cases; no duplicate ledger postings. | Repro steps, logs and database check results |
| 5 | Walk through the app in English, Gujarati and `--pseudo` at 1366×768 and 100%/125%/150% scaling, including keyboard-only use and scanner-like typed input. | No clipped controls, raw resource keys or Gujarati digits in numeric output; remaining findings are filed. | Screenshots and issue list |
| 6 | Test a clean install offline in a fresh Windows VM if one is available on this PC. If not, use a separate Windows account and isolated data root for a partial install check. | Install, update, uninstall and backup recovery are demonstrated in the chosen environment. A separate account is not counted as clean-PC proof. | VM/account setup and install log |
| 7 | Send the [Gujarati screenshot pack](gujarati-review/2026-10-08/README.md) for native review, then fix and recapture any flagged text or layout. | Reviewer records decisions for each issue; updated screenshots match the fixes. | Review notes and new captures |

Current baseline: Release build has zero warnings; 106 tests pass and one packaged journey remains skipped until a portable release exists. The screenshot pack contains 22 fresh Gujarati images.

## Work that still needs external equipment or people

Physical printer, cutter, drawer, label calibration and scanner timing need those devices. A real mains or UPS power-pull and a live store pilot need the store environment. Native Gujarati wording and accountant/business-data approval need qualified human reviewers. These checks remain open in [the release checklist](release-checklist.md).
