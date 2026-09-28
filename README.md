# MultiMerge

MultiMerge is a Visual Studio extension for TFVC that brings the changesets associated with a work item to a target branch through a guided merge workflow. It also retains the original single-changeset merge workflow.

Maintained by **Luca Persichini**. Based on [AutoMerge by Kulikov Denis (CDuke)](https://github.com/CDuke/AutoMerge), under the Apache License 2.0.

## Features

- Discover changesets linked to a work item and select the changesets to include.
- Plan merges per file and split the operation into parts when intervening changes require a check-in boundary.
- Preview the plan, review dependency warnings and resolve text conflicts in a dedicated tab.
- Configure team and personal policies: merge, discard or skip files; preserve selected target lines or blocks.
- Use presets for package references, central package management and assembly versions.
- Review manual follow-ups for changes excluded by protected-line policies.
- Check pending changes, planned merges and protected content before check-in.
- Merge a single changeset to selected branches from Team Explorer.

## Workflow

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

## Build and tests

The fork was built and its 314 automated tests passed using Visual Studio 2026 MSBuild and VSTest. The maintainer has also exercised the TFVC workflow in their own environment. Automated tests do not replace building and testing each merged application.

Use **MSBuild from Visual Studio**, with the Visual Studio extension development tools and .NET Framework targeting pack installed:

```powershell
MSBuild.exe src\MultiMerge.sln /restore /t:Build /p:Configuration=Debug /p:VisualStudioVersion=18.0
vstest.console.exe src\MultiMerge.Tests\bin\Debug\net48\MultiMerge.Tests.dll /TestAdapterPath:src\MultiMerge.Tests\bin\Debug\net48 /Framework:".NETFramework,Version=v4.8"
```

The VSIX is generated at `src\MultiMerge\bin\Debug\MultiMerge.vsix`. Version-specific manifests are retained for older Visual Studio versions; these have not all been validated for the new task workflow.

## Limits

- One target branch per task merge.
- A work item link does not prove that the task contains all of its code dependencies.
- The extension does not build the application or choose the correct conflict resolution for you.
- Task merges stop on unsupported operations such as renames, branching, rollback or undelete.
- Resuming a merge chain after closing Visual Studio is not currently supported.

## Project and attribution

- [MultiMerge repository](https://github.com/lucapersichini/MultiMerge)
- [Release notes](https://github.com/lucapersichini/MultiMerge/blob/master/RELEASE_NOTES.md)
- [Original AutoMerge project](https://github.com/CDuke/AutoMerge)

Original copyright and license notices are retained. See `LICENSE.txt` and `NOTICE.txt`.
