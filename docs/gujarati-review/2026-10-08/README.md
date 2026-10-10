# Gujarati review pack — 8 October 2026

These 22 screenshots come from the current Release build's `Shell_LoadsAndSwitchesToGujarati` UI smoke test. They use synthetic store and transaction data. The two receipt images are raster previews; physical printer output still needs a separate check.

| Folder | Screenshots | Review focus |
|---|---:|---|
| [Screens](screens/) | 16 | Setup, catalog, purchasing, stock count, money out, billing, history, settings, backup/restore |
| [Reports](reports/) | 4 | Dashboard at normal/narrow width, tabular and hourly reports |
| [Receipts](receipts/) | 2 | Sale and return receipt terminology, line layout, tax labels and English digits |

Review every image for correct Gujarati meaning, consistent terms, English digits, clipping, mixed-language text, and text that is hard to read at normal size. Record findings in the table below or attach an annotated copy. Mark a term as intentionally English when appropriate (for example, a brand name, GSTIN, MRP, or UPI).

| File | Issue / suggested Gujarati | Severity | Reviewer / date |
|---|---|---|---|
| | | | |

## Coverage limits

- Many dialogs and screens are still captured only in English by the UI smoke test. This pack is a review starting point, not complete native-language sign-off.
- The screenshots were rendered at the test window sizes and 96 DPI. The 125%/150% scaling and 1366×768 walkthrough remains open.
- The receipt previews do not validate paper width, cutter, drawer, or printer fonts.
- Final Gujarati approval requires a native speaker and a live walkthrough of paths missing from this pack.

Source: `tests/GroceryErp.UI.Tests/PhaseZeroSmokeTests.cs`. Captures were made with `GROCERY_PHASE2_SCREENSHOT_DIR`, `GROCERY_PHASE6_SCREENSHOT_DIR`, and `GROCERY_PHASE4_SCREENSHOT_DIR` in separate test runs. All three runs passed.
