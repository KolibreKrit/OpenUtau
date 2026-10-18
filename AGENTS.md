# AGENTS.md

This is KolibreKrit's fork of [OpenUtau](https://github.com/openutau/OpenUtau).

## What the fork is for

`svcover-embedding` is the fork's working branch. The app [SVCoverEditor](https://github.com/KolibreKrit/SVCoverEditor) uses it as its vocal source. Its design page there is `docs/design/openutau.md`.

`master` stays identical to openutau/OpenUtau's master. It never gets fork-only commits, and that includes this file.

## PRs and merging

PRs target `svcover-embedding`. Merge them with a merge commit, not a squash or a rebase, as PR #1 was. That keeps the SHAs the app pins, and it keeps the commits meant for upstream intact.

## Work that could go upstream

A general fix, such as the DAW Integration fixes on PR #2, goes in its own commits, apart from embedding changes. Those commits must apply to `master` and pass `OpenUtau.Test` there. They must not touch files that exist only on the fork, such as `OpenUtau/Embedding/` and this file.

Never open a PR, issue or comment on openutau/OpenUtau without the owner's go-ahead.

## Syncing with upstream

Merge openutau/OpenUtau's master into `svcover-embedding`, as 4591c69 did. Once upstream has taken a fix the fork carries, resolve conflicts toward upstream's version.

## After a merge into `svcover-embedding`

The app builds the commit its `config/build.json` pins, not the branch. A merge reaches the app only once that pin moves to the merge commit. When handing back, tell the owner the pin needs moving.

Don't build or install the fork as a standalone OpenUtau app. The installed OpenUtau stays an official release.

## Building and testing

Keep .NET's settings and packages out of the home folder. Run `dotnet` through a small wrapper that sets `HOME`, `DOTNET_CLI_HOME` and `NUGET_PACKAGES` to folders inside the worktree, such as under `artifacts/`, which git ignores. Set them for `dotnet` alone, because git and gh read `HOME` too. Don't touch `~/Library/OpenUtau`.

Run both suites. They use xunit v3, so they run with `dotnet run`.

```sh
dotnet build --disable-build-servers OpenUtau.Test/OpenUtau.Test.csproj
dotnet run --no-build --project OpenUtau.Test/OpenUtau.Test.csproj
dotnet build --disable-build-servers OpenUtau.UiTest/OpenUtau.UiTest.csproj
dotnet run --no-build --project OpenUtau.UiTest/OpenUtau.UiTest.csproj
```

Two tests are skipped unless set up. `MergeAdjacentPhrasesLiveTest` needs a DiffSinger voicebank, with `OPENUTAU_TEST_SINGERS` set to a folder that contains one. `DawRealPluginTest` needs a running bridge, with `OPENUTAU_BRIDGE_DISCOVERY` set to its discovery folder.

## Git

Never use bare `git stash` or `git stash pop`. Other worktrees share the stash.
