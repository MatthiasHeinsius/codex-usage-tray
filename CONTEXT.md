# Codex Usage Tray

This context presents the user's current Codex allowance and inference activity using account data and local activity when needed.

## Language

**Usage Snapshot**:
The best currently known usage state, combining account data, retained activity from an earlier refresh, and local activity when needed.
_Avoid_: Raw account response, usage response

**Usage Observation**:
A source-specific reading of usage facts before they are reconciled into a Usage Snapshot.
_Avoid_: Usage Snapshot, raw response

**Allowance Window**:
A time-bounded Codex allowance with a used percentage and an optional reset time.
_Avoid_: Usage Window, limit window
