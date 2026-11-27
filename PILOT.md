# PILOT.md

What a plane can and cannot do in this repo. The node quotes this page whole into the planner, worker and reviewer
briefs, so it stays short. `CLAUDE.md` holds the architecture and conventions (and points into `docs/`); this page
holds only what a flight needs to plan, build, check and land.

## What a plane can and cannot do

- A plane is a git worktree at `<repo>/.claude/worktrees/<code>` on `flight/<code>`. Work and commit only there. Do not
  push, open a PR or merge; the controller lands the flight. Leave nothing uncommitted, and put scratch output
  outside the plane.
- LALR.CC (the parser generator) is a sibling clone the plane cannot see. `UseLocalLalrCc` switches on only when
  `..\..\sharpastro\LALR.CC` exists relative to the repo root, which from a plane resolves inside `.claude\`, where
  none lives. So a plane restores the pinned NuGet package, as CI does. Never force it on with `-p:UseLocalLalrCc=true`.
  A change LALR.CC needs is `pilot follow-up SharpAstro/LALR.CC --title ...`, never `gh issue create`.
- Every build and test goes through `pilot run -- <one plain command>`; the node queues jobs per repo. Never wait
  with a job that sleeps or polls: use `pilot leg wait --for-file <path>` and end the turn.
- No GitHub writes from a plane (issues, labels, milestones, comments): name what should change in the leg's report.

## Build and check

Always `-c Release`, as CI does. Run from the repo root. Warnings are not errors (`TreatWarningsAsErrors` is false
in `Directory.Build.props`, and CI does not override it), but a leg adds no new warnings.

```bash
dotnet build dotcc.sln -c Release
dotnet test DotCC.Tests/DotCC.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~<Class>"
dotnet test DotCC.FunctionalTests/DotCC.FunctionalTests.csproj -c Release --no-build --filter "FullyQualifiedName~<Class>"
```

- Tests are xunit.v3 with Shouldly on VSTest (`xunit.runner.visualstudio`): narrow with `--filter`. A leg runs the
  test classes it touches, plus the unit suite when it changes the emitter or runtime. The whole functional suite is
  CI's, on the PR: never run `Scripts/run-functional-sharded.sh` from a plane (it fills every core).
- A functional fixture is a folder under `DotCC.FunctionalTests/Fixtures/` with its sources and `expected-stdout.txt`.
  A fixture must never call `exit()` (it ends the in-process test host). A fixture that also compiles under
  `--target=wat` must be listed in `DotCC.FunctionalTests/wat-fixtures.txt`, or CI's wat ratchet fails.
- Unit tests that regex an emitted program see the whole spliced runtime (`DotCC.Libc/*.cs` is embedded into every
  emit), so new runtime code can trip an unrelated pin: run the unit suite after a runtime change.
- The differential oracles switch on through environment variables. A `pilot run` job gets the node's environment,
  not the plane's, so pass the variable with `dotnet test -e`, which sets it for the test process: a plane runs the
  oracle that covers its change when the machine has its tool, narrowed with `--filter`, for example
  `dotnet test DotCC.FunctionalTests/DotCC.FunctionalTests.csproj -c Release --no-build -e DOTCC_RUN_GCC_ORACLE=1 --filter "FullyQualifiedName~GccWslOracleTests"`
  (pass `DOTCC_ZIG_LIB_DIR` and the like with `-e` too). The leg's report names every oracle it did not run; CI
  runs them all on the PR. What each needs:
  - `DOTCC_RUN_GCC_ORACLE=1`: gcc (WSL Ubuntu on Windows).
  - `DOTCC_RUN_MSVC_ORACLE=1`: `cl.exe` from a Visual Studio developer environment.
  - `DOTCC_RUN_ZIG_ORACLE=1`: `zig` 0.16.0, CI's pin.
  - `DOTCC_RUN_WAT=1`: wabt's `wat2wasm` and `node` on PATH (Windows has node but no `wat2wasm`).
  - `DOTCC_RUN_SHARED_LIB_ORACLE=1`: a NativeAOT publish (`vswhere` on PATH on Windows) and a C compiler.
  - `DOTCC_RUN_CPYTHON_ORACLE=1`: gcc and the CPython headers.
  - `DOTCC_REGEN_BASELINE=1` rewrites committed snapshots: only in a leg that is about the snapshot.
- The real-world suites (Lua, chibi-scheme, CPython, Goose) are CI workflows (`lua.yml`, `chibi.yml`, `python.yml`,
  `goose.yml`). A leg that changes the emitter, the runtime or a header names them in its report.
- After a `.lalr.yaml` edit: a full build, never `--no-build` (a stale parse table makes a right grammar look
  wrong). The resolved-conflict dump diff against main's needs `LALRCC_RESOLVED_DUMP` set at build time, which a
  plane cannot do (`dotnet build` has no `-e`; SharpAstro/LALR.CC#12 would make it an MSBuild property), so the
  leg's report flags the grammar edit and the controller runs the diff (recipe in `docs/architecture.md`, Grammar
  conventions): every new line is a silently settled conflict to justify.
- Package versions live in `Directory.Packages.props`, never in a `.csproj`. The SDK is pinned in `global.json`.

## How a flight merges

- main requires linear history (no merge commits), and a multi-commit PR lands by **rebase merge**, each commit
  kept. So a flight's branch is a straight line of commits on main. Take main with `git rebase origin/main`, never a merge, because a merge commit cannot be rebased onto main. A
  rebase that hits a conflict in someone else's change is reported, not resolved by guessing.
- Every commit lands on main as it is, so keep a flight's commits few, each one whole, and each one building.
- When a flight lands a feature or closes a gap, it updates the doc that tracks it in the same flight:
  `docs/C-SUPPORT.md` or `docs/ZIG-SUPPORT.md`, and the plan's row in `docs/plans/summary.md`.

## Writing

No em-dashes, and no `--` in their place: a full stop, a comma, a semicolon or parentheses. This holds for prose,
comments and commit messages. In C#, no null-forgiving `!`: check for null, or pattern-match. Document new methods
with `/// <summary>` comments.
