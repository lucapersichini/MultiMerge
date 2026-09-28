# MultiMerge

**Guided TFVC merges by work item, with per-file planning and configurable merge policies.**

[Install from Visual Studio Marketplace](https://marketplace.visualstudio.com/items?itemName=lucapersichini.MultiMerge) · [Release notes](RELEASE_NOTES.md) · [Apache 2.0 license](LICENSE.txt)

MultiMerge brings the changesets associated with a work item to a target branch through a guided workflow in Visual Studio. Select the changes to include, review the merge plan, resolve conflicts and check in each part. The original single-changeset merge workflow is also available.

Maintained by **Luca Persichini**. Based on [AutoMerge by Kulikov Denis (CDuke)](https://github.com/CDuke/AutoMerge), under the Apache License 2.0.

## Install

1. Download MultiMerge from the [Visual Studio Marketplace](https://marketplace.visualstudio.com/items?itemName=lucapersichini.MultiMerge).
2. Close Visual Studio and install the downloaded VSIX.
3. Reopen Visual Studio, connect to your TFVC project and open **Team Explorer → MultiMerge**.

The workflow has been tested on **Visual Studio 2026 Professional / Enterprise on Windows (64-bit)**. You need a TFVC workspace that maps the target branch. The Marketplace manifest also permits Visual Studio 2022 17.14+, which has not yet been validated by the maintainer.

## Features

- Discover changesets linked to a work item and select the changesets to include.
- Plan merges per file and split the operation into parts when intervening changes require a check-in boundary.
- Preview the plan, review dependency warnings and resolve text conflicts in a dedicated tab.
- Configure team and personal policies: merge, discard or skip files; preserve selected target lines or blocks.
- Use presets for package references, central package management and assembly versions.
- Review manual follow-ups for changes excluded by protected-line policies.
- Check pending changes, planned merges and protected content before check-in.
- Merge a single changeset to selected branches from Team Explorer.

## A look at the workflow

The screenshots below have identifying project, user and changeset details redacted. They were captured before the MultiMerge rename, so some panels still show the former **Auto Merge** name.

### 1. Open a task merge from Team Explorer

Enter the work item ID and target branch, then open the dedicated merge tab.

<img src="screenshots/multimerge-team-explorer.png" alt="Team Explorer entry point with work item and target branch fields" width="420">

### 2. Choose the changesets to include

Review the changesets associated with the work item. Excluding a changeset recomputes the plan and preview.

![Work item changeset selection with identifying details redacted](screenshots/multimerge-changesets.png)

### 3. Review the plan and track progress

Inspect the file-by-file plan, dependency warnings, progress and check-in boundaries before proceeding through the merge.

![Per-file merge plan, warnings and progress with identifying details redacted](screenshots/multimerge-merge-plan.png)

### 4. Configure team and personal policies

Choose which files to merge, discard or skip, and which lines to preserve. Presets help configure package references and assembly versions.

![Team and personal merge policy editor with identifying paths redacted](screenshots/multimerge-policies.png)

## Run a merge

1. Open **Team Explorer → MultiMerge → Merge From Task**.
2. Select a work item and target branch, then open the **Merge from Task** tab.
3. Review the plan and configure **Merge Policies** as needed.
4. Start the merge and resolve conflicts.
5. Build and test the target branch before confirming each check-in. Continue until all parts are complete.
6. Review the manual follow-ups and update branch-specific packages through your normal process.

## Merge policies

Team rules live in `.automerge-policy.json` at the target branch root. The existing filename is retained for compatibility with policies created before the rename. Save and check in that file separately before starting the merge.

Personal rules live in `%APPDATA%\MultiMerge\merge-policy.personal.json`. The existing `%APPDATA%\AutoMerge` policy file is copied on first use if no MultiMerge file exists. Personal rules take precedence over team rules.

| Action | Content | TFVC merge history |
| --- | --- | --- |
| Merge | Merge changes, preserving configured protected content | Recorded through the merge workflow |
| Discard | Keep target content | Record the merge as discarded |
| Skip | Leave the file alone | No merge recorded |

Legacy single-changeset preferences are copied from `%APPDATA%\Visual Studio Auto Merge\automerge.conf` to `%APPDATA%\Visual Studio MultiMerge\multimerge.conf` on first use.

## Limits

- One target branch per task merge.
- A work item link does not prove that the task contains all of its code dependencies.
- The extension does not build the application or choose the correct conflict resolution for you.
- Task merges stop on unsupported operations such as renames, branching, rollback or undelete.
- Resuming a merge chain after closing Visual Studio is not currently supported.

## Build and tests

The fork was built and its 314 automated tests passed using Visual Studio 2026 MSBuild and VSTest. The maintainer has also exercised the TFVC workflow in their own environment. Automated tests do not replace building and testing each merged application.

Use **MSBuild from Visual Studio**, with the Visual Studio extension development tools and .NET Framework targeting pack installed:

```powershell
MSBuild.exe src\MultiMerge.sln /restore /t:Build /p:Configuration=Debug /p:VisualStudioVersion=18.0
vstest.console.exe src\MultiMerge.Tests\bin\Debug\net48\MultiMerge.Tests.dll /TestAdapterPath:src\MultiMerge.Tests\bin\Debug\net48 /Framework:".NETFramework,Version=v4.8"
```

The VSIX is generated at `src\MultiMerge\bin\Debug\MultiMerge.vsix`. Use `/p:Configuration=Release` to create the distribution package at `src\MultiMerge\bin\Release\MultiMerge.vsix`. Version-specific manifests are retained for older Visual Studio versions; these have not all been validated for the new task workflow.

## Project and attribution

- [Visual Studio Marketplace](https://marketplace.visualstudio.com/items?itemName=lucapersichini.MultiMerge)
- [MultiMerge repository](https://github.com/lucapersichini/MultiMerge)
- [Release notes](RELEASE_NOTES.md)
- [Original AutoMerge project](https://github.com/CDuke/AutoMerge)

Original copyright and license notices are retained. See [LICENSE.txt](LICENSE.txt) and [NOTICE.txt](NOTICE.txt).
