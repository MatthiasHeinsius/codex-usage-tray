# Repository maintenance

## Dependencies

Dependabot checks NuGet packages, GitHub Actions, and the .NET SDK weekly. SDK updates change `global.json`; they do not update the separately pinned runtime in `src/CodexUsageTray/CodexUsageTray.csproj`.

The application and test projects each have a `packages.lock.json` recording exact package versions and content hashes. The application lock includes the `win-x64` release graph and the ILLink build dependency used for single-file publishing. Ordinary restore keeps these versions when package requirements have not changed. CI and `build-release.ps1` require locked-mode restore.

After changing a package version or the runtime pin, run `dotnet restore .\CodexUsageTray.slnx` to update the affected lock files, then review and commit their diffs with the version change. Verify with the checks in `CONTRIBUTING.md` and `.\build-release.ps1`. `dotnet restore --force-evaluate` recalculates the graph from upstream requirements and can reset transitive-only updates to older minimum versions. Update the license inventory when its contents change.

The Security maintenance workflow checks Microsoft's release metadata on pull requests, pushes to `main`, and Mondays at 05:43 UTC. It fails when the runtime pin differs from the latest patch in its .NET channel, or the channel is out of support. The release workflow runs the same check before publishing. A failure requires a maintenance PR that updates the runtime pin and reviews the complete license inventory in `THIRD-PARTY-NOTICES.txt` and `docs/license-audit.md`. Keep the pin explicit so a runtime update cannot silently change the bundled notice requirements.

On `main`, Security maintenance restores the solution and Windows release runtime packs with `SelfContained=true`, `PublishSingleFile=true`, and `IncludeNativeLibrariesForSelfExtract=true`, then submits resolved NuGet dependencies to GitHub. These properties match the release build and include its ILLink build dependency. The repository's `dependency-snapshot.ps1` reads NuGet's generated `project.assets.json` files, including exact versions, dependency relationships, and downloaded framework packs. It uses each target's runtime assets and framework references to distinguish runtime dependencies from development tooling and unused downloaded packs. This supplies dependencies that static manifest scanning misses. The submission job has `contents: write` and never runs on pull requests. PR builds validate snapshot generation without submitting it. Other validation jobs use read-only repository permissions.

Both workflows run `test-dependency-snapshot.ps1` against the generated inventory. The checks require the two shipped runtime packs, ILLink as a development dependency, and test dependencies with development scope. Update these expectations if the application's actual dependencies change.

After merging a dependency change, check that the submission succeeds and inspect GitHub's dependency graph and Dependabot alerts. A failed submission leaves the previous inventory in place.

## Pull requests and access

The active `main` ruleset requires a current successful `build`, resolved review conversations, and CodeQL results without error-level findings or medium-or-higher security findings. It blocks direct pushes, force pushes, and deletion, with no bypass actors. Zero review approvals are required because the repository has one maintainer. Only squash merging is enabled. Auto-merge is available but must be selected per PR.

The owner can edit repository settings, so rules protect routine changes but cannot prevent the owner from disabling them. Future collaborators receive write access and can merge their own PRs when the rules pass. Public contributors can propose changes through forks but cannot merge into this repository. All external contributors need approval to run fork PR workflows.

GitHub Actions permits local actions and GitHub-owned actions. External action references must use full commit SHAs. This allowlist does not restrict commands executed by workflow scripts.

## Releases

Only the repository owner can create `v*` tags. A separate ruleset prevents anyone from updating or deleting those tags. The release workflow checks that the tagged commit is already in `main` and that the tag matches the application version. Push a new version tag only after the version change and its checks have passed on `main`.

The release build validates the solution and published executable before generating provenance. Only that build receives attestation and OIDC write permissions. A separate job receives `contents: write` to publish the assets. Its artifact download explicitly fails on a digest mismatch before publication. Immutable releases preserve the published tag and asset contents. The download-verification commands are in the README.

Immutable releases automatically receive a GitHub Release attestation binding the tag, commit, and asset digests. The updater cryptographically verifies that attestation against GitHub's Sigstore trust root before installing the executable. `src/CodexUsageTray/Updates/GitHubTufRoot.json` is the pinned bootstrap root for GitHub's TUF repository, copied from GitHub CLI v2.100.0; the updater uses TUF to obtain current trust material. The release workflow still generates a separate build provenance attestation for manual verification. Check that a release is immutable and that `gh release verify-asset vX.Y.Z CodexUsageTray.exe --repo MatthiasHeinsius/codex-usage-tray` succeeds before announcing it.

## Actions storage

### Process cancellation diagnostics

The manually triggered `Process cancellation diagnostics` workflow defaults to running the two-second app-server timeout case first on each fresh runner, then repeating the full test suite three times. It uses four runners: two with coverage and two without. The initial case checks that startup fits inside the deadline without a previous test warming the fixture. Disable `cold_start` to reproduce the normal suite order. Select `Targeted` to run the caller-cancellation and timeout cases after that initial check. An optional full-suite filter can isolate preceding tests but must retain both cases. Each attempt uses a fresh test process. Per-runner artifacts save console output, TRX results, coverage reports where applicable, and `summary.csv` for seven days. A failed attempt remains a failure even if later attempts pass.

To run locally, build the test project in Release, then run `./.github/scripts/diagnose-process-tests.ps1`. Its default is ten targeted attempts with coverage followed by ten without. Use `-Scope Full -Coverage On -Repetitions 3` for full-suite runs with coverage, `-Coverage Off` for the comparison, and `-Filter <expression>` for isolation. Use `-ResultsDirectory <new-directory>` for a subsequent run. Existing attempt directories are rejected to avoid mixing old and new results.

The process tests build and copy `CodexUsageTray.ProcessFixture` into their output directory. Its app-server mode reads JSON lines and simulates replies, hangs, and disconnects. Its hold mode publishes its PID, emits `ready`, and exits after twenty seconds if cleanup fails. Tests launch it through a batch file to exercise the production command interpreter and process-tree cleanup. The helper is a test-only build dependency and is not included in the application release.

The cancellation tests check that a blocked app-server exchange stops after caller cancellation or its deadline, and that the child exits. To run the initial deadline case locally, use `-Scope Cold -Coverage On -Repetitions 1` with a new results directory.

### Native tray integration tests

`GuidNotifyIconTests` sends Windows messages to callback windows created on its own STA thread. Each test uses a separate tray GUID, so it does not share the running application's tray identity. When Explorer is available, the recovery test also verifies registration through `Shell_NotifyIconGetRect`, removes only its own icon, sends `TaskbarCreated` to its callback window, and checks that registration recovers. It does not restart Explorer. Without a taskbar, the test checks native message handling and window cleanup; its output records which checks ran.

`TrayApplicationContextTests` uses the real shell and update coordinators with isolated registry settings and controlled observation/update sources. These tests cover startup preferences, cleanup after initialization fails, and cancellation before shell disposal during shutdown.

`UsagePopupFormTests` connects a real session-file monitor to the popup on an STA thread. It checks UI dispatch and disposal with an update already queued. Focus-loss tests deliver the form's deactivation event and let its real timer run, so they do not depend on Windows granting foreground activation.

`ProgramTests` starts a helper process that briefly holds the application's single-instance mutex, then launches the built application and checks that it exits without disturbing the helper. It tests duplicate-instance startup without starting a full tray session or changing user settings. Both child processes have bounded waits and cleanup on failure.

`UpdateInstallerTests` simulates partial failure at the file-replacement call. File preparation, backup restoration, cleanup, and restart decisions still run against real temporary files. These cases cover both an absent destination and a destination already replaced when Windows reports failure.

### Artifact retention

Build runs on pull requests and pushes to `main`. Superseded runs are cancelled. Only `main` builds upload portable executables; test results and these executables expire after seven days. Release staging artifacts expire after one day. The repository default for future artifacts and logs is seven days. GitHub release assets are separate and remain available.

Changing retention does not alter expiry dates on existing artifacts. Existing artifacts keep their original expiry dates.

## Account checks

In GitHub account settings, keep two-factor authentication enabled, preferably with a passkey or hardware security key, and retain recovery codes. Review installed GitHub Apps and authorized OAuth apps for unnecessary repository access. Repository API access alone cannot verify these account-level controls.
