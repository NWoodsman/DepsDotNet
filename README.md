# DepsDotNet

Commit a set of interdependent .NET projects that live in separate git repos, in dependency order, so that every repo records the exact commit of each repo it depends on.

Given a solution or project, DepsDotNet works out which git repos own the projects in its `ProjectReference` graph, and commits them so that dependencies always come first. Each repo that depends on others gets a `Dependencies.json` at its root mapping each dependency repo to the commit hash it was just built against. A dependency's new hash only exists once it has committed, so the hashes are collected one repo at a time as the commits happen.

## Requirements

- .NET 10 SDK (`dotnet` on `PATH`). The tool runs `dotnet msbuild` and `dotnet sln` to read your projects.
- git on `PATH`.
- The input must be restored (`dotnet restore`, or any build) so each project has a `project.assets.json`.

## Build

```sh
dotnet publish src/DepsDotNet -c Release -p:PublishSingleFile=true --self-contained false
```

This produces one executable at `artifacts/publish/DepsDotNet/release/DepsDotNet` (`DepsDotNet.exe` on Windows). 

The executable uses the installed .NET runtime. If it reports `You must install .NET to run this application`, set `DOTNET_ROOT` to your .NET install. This is common with Nix and other non-standard installs.

## Usage

```
DepsDotNet <solution (.sln/.slnx) | project file | project.assets.json> (-m <message> | --dry-run) [--force]
```

| Option | Meaning |
|---|---|
| `-m`, `--message <msg>` | Commit each repo that has changes, in dependency order, with this message. |
| `--dry-run` | Show what would be committed. Reports untracked files or unstaged changes. |
| `--force` | Stage all changes, including untracked files, without asking. |

The input can be:

- **A solution (`.sln` or `.slnx`).** Every project in it is included.
- **A project file.** That project and everything it references, directly or indirectly.
- **A `project.assets.json`.** The project that owns it, and everything it references.

### Typical workflow

```sh
dotnet restore MyApp.slnx
depsdotnet MyApp.slnx --dry-run          # look first
depsdotnet MyApp.slnx -m "Update canvas API"
```

## How a run works

1. **Read the project graph.** For each project, the tool finds its `project.assets.json` through MSBuild (so custom output locations like `ArtifactsPath` work). It reads that project's references from the file. Package references are ignored. References are combined across all target frameworks.
2. **Find each project's repo.** Starting at the project's folder, it walks up to the nearest folder containing `.git`. Worktrees and submodules, which use a `.git` file, are handled; a project in a submodule belongs to the submodule. A project outside any repo is an error.
3. **Order the repos.** Each project reference that crosses repos becomes a dependency between those repos, and the repos are sorted so every repo comes after the repos it depends on. If two repos each depend on the other, no order can record correct hashes. The tool refuses and names the project references that cause the loop.
4. **Check every repo.** The run stops before asking or changing anything if any repo has a merge, rebase, cherry-pick or revert in progress, unresolved conflicts, a detached HEAD, or a branch with no commits.
5. **Ask about every repo (confirmation pass).** See [Cautions](#cautions). All answers are collected before anything is staged or committed.
6. **Commit pass.** For each repo in order:
   1. Write `Dependencies.json` using the hashes collected so far.
   2. Stage according to your answers.
   3. Commit if anything is staged.
   4. Record the repo's HEAD, new or unchanged, for the repos after it.

DepsDotNet commits what you staged, or if you run the tool, you can decide on what needs to be staged as you go.

## Cautions

Without `--force`, a run asks you questions when you reach a repo that has unstaged or untracked files:

```
! RepoX has unstaged changes. Do you want to stage them? y/n/a
! RepoX has untracked files. Do you want to track and stage them? y/n/a
```

| Answer | Unstaged changes | Untracked files |
|---|---|---|
| `y` | Staged (`git add -u`) and committed | Staged and committed |
| `n` | Left out of the commit | Left untracked |
| `a` | Abort the whole run; nothing is committed | Same |

Files covered by `.gitignore` never count as untracked. If input is closed (for example, run from a script with no stdin), the run aborts with nothing committed. In scripts, use `--force` or pipe the answers in:

```sh
printf 'y\nn\n' | depsdotnet MyApp.slnx -m "msg"
```

## Dry run

`--dry-run`  shows:

- HEAD, branch, and the projects the repo owns.
- What would be committed.
- A caution for each decision a real run will ask about, with the files and what each answer would do:
  ```
  Caution: RepoY has untracked files which will require a decision.
    ?? notes.txt
    (y: track and commit them, n: leave them untracked, a: abort the whole run;
     or add them to .gitignore if they should never be committed)
  ```
- Whether `Dependencies.json` would be created, updated, deleted or left unchanged, with its new content.
- Any problem that would stop a real run.

The preview assumes every question is answered `n`. A repo that would commit only if you answered `y` (to its own questions, or to those of a repo it depends on) is marked as such. A repo that would commit shows up in its dependents' `Dependencies.json` as a placeholder, `<new commit of RepoY>`, because the real hash doesn't exist until the commit happens.

## `Dependencies.json`

Written at the root of each repo that depends on other repos:

```json
{
  "../Foo": "4ccffa7c9d2f61a8b0c3e5d7f9a1b2c3d4e5f6a7",
  "../Bar": "11d700096f360bd9a34e6b623aaa31ec533c747d"
}
```

- **Keys** are the path from this repo to the dependency repo, using `/`.
- **Values** are the dependency's commit at the time this repo committed.

If a repo no longer depends on any other repo, its `Dependencies.json` is deleted.

The tool stages this file itself. Its working-copy state doesn't matter, because it's compared with the version committed at HEAD.

## Failures and reruns

If a commit fails partway, for example because a pre-commit hook rejects it, the run stops and reports which repos were already committed. Fix the problem and run the same command again. Repos that already committed are now clean, so they record their existing HEAD without committing again, and the run carries on from where it stopped.

The `Dependencies.json` written for the failed repo stays staged until the rerun.

## Exit codes

| Code | Meaning |
|---|---|
| 0 | Success, or a dry run with no blocking problems. |
| 1 | Bad arguments, a blocking problem, an abort, closed input during a question, or a failed commit. |

## Development

```sh
dotnet build
dotnet run --project src/DepsDotNet -- path/to/MyApp.slnx --dry-run
```

| File | Role |
|---|---|
| `ProjectGraph.cs` | Reads `project.assets.json` files (and solutions) into the project graph. |
| `RepoPlan.cs` | Maps projects to git repos and orders the repos. |
| `TopoSorter.cs` | Depth-first topological sort that reports dependency loops. |
| `CommitRunner.cs` | Repo checks, the dry-run simulation and the commit pass. |
| `DependencyFile.cs` | Writes `Dependencies.json`. |
| `Git.cs` | Runs the git command line directly, without a shell. Every call uses `git -C <repo>` and reads machine-readable output or exit codes. |
| `Program.cs` | Arguments, questions and output. |

## License

MIT. See [LICENSE](LICENSE).
