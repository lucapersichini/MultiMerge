# MultiMerge

**Move the changes that belong to a task between branches in Visual Studio, using Git or TFVC.** MultiMerge detects the active version-control context and opens the matching workflow. You choose the changes, inspect the plan and resolve conflicts before completing the transfer.

## What you can do

- **Select the work behind a task.** Choose linked TFVC changesets, or identify Git commits from a task ID in commit messages or Azure Boards links. Adjust the selection before applying it.
- **Preview branch impact.** TFVC shows a file-by-file plan, dependency warnings and check-in boundaries. Git previews selected commits for each destination in an isolated clone.
- **Protect branch-specific content.** Team and personal policies can merge, discard or skip paths and preserve selected lines. Review follow-ups when protected content needs manual attention.
- **Resolve text conflicts in Visual Studio.** Continue after staging a resolution. An active Git transfer can be recovered after a Visual Studio restart.
- **Transfer to multiple Git branches in sequence.** Later targets wait when a conflict occurs. Merge commits require an explicit parent choice; MultiMerge never pushes branches automatically.

## TFVC workflow

Open **Team Explorer → MultiMerge → Merge From Task**, enter a work item and target branch, then review the linked changesets. Include or exclude several rows at once, update the plan, resolve conflicts and check in each part. Comments are prefilled from the task and included changesets. The original single-changeset merge workflow remains available.

![Redacted TFVC task merge plan](https://raw.githubusercontent.com/lucapersichini/MultiMerge/master/screenshots/multimerge-merge-plan.png)

## Git workflow

Open MultiMerge in a Git solution and choose the local source and target branches. Load commits, optionally select those associated with a task, then **Update plan**. Review each target preview before applying. For a merge commit, choose the mainline parent explicitly. When text conflicts occur, edit and stage the result in the dedicated view, then continue. MultiMerge saves an active Git transfer in the repository so it can be recovered after restarting Visual Studio.

Git transfers require Git for Windows, a configured author identity and a clean working tree before application. MultiMerge does not infer every code dependency, build the application or push to a remote. Build and test each destination before pushing it.

## Policies and support

For either mode, the team policy lives in `.automerge-policy.json` at the target branch root. Personal rules override matching team rules. See the [README for the 1.2.0 source branch](https://github.com/lucapersichini/MultiMerge/tree/feature/git-preview) for the policy schema, workflow details and current limitations, or the [release notes](https://github.com/lucapersichini/MultiMerge/blob/feature/git-preview/RELEASE_NOTES.md) for changes in this version.

MultiMerge is maintained by Luca Persichini and builds on [AutoMerge by Kulikov Denis (CDuke)](https://github.com/CDuke/AutoMerge). It retains the original Apache 2.0 license and notices.
