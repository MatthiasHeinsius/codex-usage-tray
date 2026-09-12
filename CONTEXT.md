# Codex Usage Tray

This context presents the user's current Codex allowance and inference activity using account data and local activity when needed.

## Language

**Usage Snapshot**:
The best currently known usage state, combining account data, retained activity from an earlier refresh, and local activity when needed.
_Avoid_: Raw account response, usage response

**Usage Observation**:
A source-specific reading of usage facts before they are reconciled into a Usage Snapshot.
_Avoid_: Usage Snapshot, raw response

**Usage Update**:
One requested refresh that produces a final Usage Snapshot and its corresponding user-facing presentation after any Allowance Window Activation work finishes.
_Avoid_: Refresh cycle, display cycle

**Usage Presentation**:
The complete user-facing state of Codex usage, including loading, current or stale values, failure context, tray display, popup display, and notices.
_Avoid_: UI state, view model, display data

**Observation Time**:
The time at which a Usage Observation read a usage fact. A retained fact keeps its original Observation Time when it appears in a later Usage Snapshot.
_Avoid_: Updated time, retrieval time

**Allowance Window**:
A time-bounded Codex allowance with a used percentage and an optional reset time.
_Avoid_: Usage Window, limit window

**Unused Allowance Window**:
An Allowance Window with no recorded usage and its full allowance remaining.
_Avoid_: Expired Allowance Window, unstarted window

**Used-up Allowance Window**:
An Allowance Window whose recorded usage has consumed the full allowance.
_Avoid_: Expired Allowance Window, unavailable window

**Allowance Window Activation**:
The transition of an Unused Allowance Window into active use, confirmed when its reset time changes. A successful inference request alone does not confirm activation.
_Avoid_: Successful request, window start

**Allowance Window Reset**:
The natural transition from a Used-up Allowance Window to a new Unused Allowance Window.
_Avoid_: Allowance Window Activation, reset-time change
