@AGENTS.md

## Fork ownership (placemaker-advanced)

- This fork belongs to the GitHub account **melv-narrow**, not melvin-uponai.
  - `origin` = `https://melv-narrow@github.com/melv-narrow/placemaker-advanced` (public fork, created 2026-11-10).
  - `upstream` = `https://github.com/jackwrichards/placemaker` (read-only, original author).
- Commits use the repo-local identity `Melvin Mpolokeng <95126917+melv-narrow@users.noreply.github.com>`.
  Never change it to a work address.
- Before any `gh` command that writes (fork, PR, release), run `gh auth switch -u melv-narrow`. The machine's
  default active account is melvin-uponai. Switch back to melvin-uponai when done.
- Push without credential prompts (uses the active gh account, so switch first):
  `git -c credential.helper= -c "credential.helper=!gh auth git-credential" push`
- No AI attribution in commits or PRs (no Co-Authored-By trailer, no tool footer).
- Target game version: Cities: Skylines II **1.6.3f1** (upstream was written against 1.6.2f1).
- Fork docs live in `docs/fork/`: `SPEC.md` (what and acceptance), `PLAN.md` (order of work), `PLAN-v1.md`
  (original brief). Upstream docs in `docs/` stay as the original author wrote them.
- Machine paths (AGENTS.md still shows the original author's; these override it):
  - Game runner results: `%USERPROFILE%\.placemaker\suite\runs` (override with env `PLACEMAKER_RESULTS_ROOT`).
  - Game install: default Steam path (override with env `CS2_GAME_PATH`).
  - Decompiled 1.6.3f1 `Game.dll`: `C:\Users\Melvin\Coding\Experimental\cs2-decomp\1.6.3f1\game`
    (regenerate with `ilspycmd -p -o <dir> <Game.dll>`; ilspycmd 9.1 is installed, newer needs SDK 10).
- This machine has no NuGet source configured: pass `-p:RestoreSources=https://api.nuget.org/v3/index.json` to
  `dotnet build` (or add a source once, with the owner's OK).
