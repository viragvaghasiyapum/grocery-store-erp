# Revised implementation: Phase 0

- [Behavior matrix and feature removal register](behavior-matrix.md)
- [Reproducible dependency inventory](dependency-inventory.md) — regenerate with `scripts/phase0-dependency-inventory.ps1`
- [Schema contract and migration ordering](schema-contract.md)
- [Acceptance test matrix](test-matrix.md)
- [Verified baseline, resource budgets and rollback](baseline-and-rollback.md)
- [Completion audit and open gates](completion-audit.md)

The [revised implementation plan](../../revised-implementation-plan.md) is the single source of truth for target behavior, phase scope and acceptance gates. These files provide implementation analysis, inventories, tests and measured evidence for that plan. The direct-lot engine, independent annual locks and screen removals are subsequent phases; no live business schema or existing migration was changed in Phase 0.
