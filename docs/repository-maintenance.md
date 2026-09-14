# Repository maintenance

## Dependencies

Dependabot checks NuGet packages, GitHub Actions, and the .NET SDK weekly. SDK updates change `global.json`; they do not update the separately pinned runtime in `src/CodexUsageTray/CodexUsageTray.csproj`.

The Security maintenance workflow checks Microsoft's release metadata on pull requests, pushes to `main`, and Mondays at 05:43 UTC. It fails when the runtime pin differs from the latest patch in its .NET channel, or the channel is out of support. The release workflow runs the same check before publishing. A failure requires a maintenance PR that updates the runtime pin and reviews the complete license inventory in `THIRD-PARTY-NOTICES.txt` and `docs/license-audit.md`. Keep the pin explicit so a runtime update cannot silently change the bundled notice requirements.

On `main`, Security maintenance restores the solution and Windows release runtime packs with `SelfContained=true` and `PublishSingleFile=true`, then submits resolved NuGet dependencies to GitHub. These properties match the release build and include its ILLink build dependency. The repository's `dependency-snapshot.ps1` reads NuGet's generated `project.assets.json` files, including exact versions, dependency relationships, and downloaded framework packs. It uses each target's runtime assets and framework references to distinguish runtime dependencies from development tooling and unused downloaded packs. This supplies dependencies that static manifest scanning misses. The submission job has `contents: write` and never runs on pull requests. PR builds validate snapshot generation without submitting it. Other validation jobs use read-only repository permissions.

Both workflows run `test-dependency-snapshot.ps1` against the generated inventory. The checks require the two shipped runtime packs, ILLink as a development dependency, and test dependencies with development scope. Update these expectations if the application's actual dependencies change.

After merging a dependency change, check that the submission succeeds and inspect GitHub's dependency graph and Dependabot alerts. A failed submission leaves the previous inventory in place.

## Pull requests and access

The active `main` ruleset requires a current successful `build`, resolved review conversations, and CodeQL results without error-level findings or medium-or-higher security findings. It blocks direct pushes, force pushes, and deletion, with no bypass actors. Zero review approvals are required because the repository has one maintainer. Only squash merging is enabled. Auto-merge is available but must be selected per PR.

The owner can edit repository settings, so rules protect routine changes but cannot prevent the owner from disabling them. Future collaborators receive write access and can merge their own PRs when the rules pass. Public contributors can propose changes through forks but cannot merge into this repository. All external contributors need approval to run fork PR workflows.

GitHub Actions permits local actions and GitHub-owned actions. External action references must use full commit SHAs. This allowlist does not restrict commands executed by workflow scripts.

## Releases

Only the repository owner can create `v*` tags. A separate ruleset prevents anyone from updating or deleting those tags. The release workflow checks that the tagged commit is already in `main` and that the tag matches the application version. Push a new version tag only after the version change and its checks have passed on `main`.

The release build validates the solution and published executable before generating provenance. Only that build receives attestation and OIDC write permissions. A separate job receives `contents: write` to publish the assets. Its artifact download explicitly fails on a digest mismatch before publication. Immutable releases preserve the published tag and asset contents. The download-verification commands are in the README.

## Actions storage

Build runs on pull requests and pushes to `main`. Superseded runs are cancelled. Only `main` builds upload portable executables; test results and these executables expire after seven days. Release staging artifacts expire after one day. The repository default for future artifacts and logs is seven days. GitHub release assets are separate and remain available.

Changing retention does not alter expiry dates on existing artifacts. Existing artifacts keep their original expiry dates.

## Account checks

In GitHub account settings, keep two-factor authentication enabled, preferably with a passkey or hardware security key, and retain recovery codes. Review installed GitHub Apps and authorized OAuth apps for unnecessary repository access. Repository API access alone cannot verify these account-level controls.
