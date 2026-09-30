# MultiMerge release notes

## 1.2.0 (Git workflow expansion, 2026-09-30)

- Add Git team and personal policies using the existing path and protected-line JSON rules, with a validating WPF editor and policy fingerprint checks between preview and apply.
- Select task commits from exact IDs in commit messages and linked Azure Boards Git commit artifacts; the user reviews the result before applying.
- Preview and apply selected commits sequentially to multiple local target branches, pausing before later targets when one conflicts.
- Support merge commits after an explicit mainline parent choice; preview shows the resulting changes.
- Persist transfer and batch state in the Git directory and verify the repository when recovering after a Visual Studio restart. Reject unexpected branch, policy or cherry-pick changes.
- No automatic push or dependency guarantee; multi-target application retains earlier completed branches if a later branch stops.
## 1.1.0 (Git preview)

- Automatically show a single Git or TFVC workflow from the solution/provider context; show explicit choices for unavailable, multiple or conflicting contexts. Keep the context locked during transfers and discard stale detection results.
- Add Git local repository/branch selection and Ctrl/Shift commit selection.
- Preview cherry-picks in an isolated temporary clone, then apply approved commits locally with original messages and source SHA provenance.
- Detect patch-equivalent commits, refuse dirty working trees and invalidate plans when source or target branches move.
- Resolve UTF-8 text conflicts in current/incoming/result panels, save and stage, continue or abort the current cherry-pick.
- First Git version limits: local branches, up to 200 commits, no Git policies or work-item discovery yet, no merge commits requiring mainline selection, no session restore after restarting Visual Studio. Abort retains earlier completed commits. Nothing is pushed automatically.

## 1.0.2 (2026-09-28)

- Populate check-in comments with the original comments of the changesets delivered in each part, including partial check-ins and Pending Changes.
- Replace the blocking Start warnings dialog with a resizable, minimizable window listing all warnings. Visual Studio remains usable while the merge waits for explicit confirmation.

## 1.0.1 (2026-09-28)

- Select multiple task changeset rows with Ctrl/Shift and include or exclude them together.
- Defer checkbox changes until Update plan, avoiding repeated TFVC previews.
- Keep Start disabled and label the previous plan while the selection has unapplied changes.

## 1.0.0 (2026-09-28)

- Establish MultiMerge as an independent fork maintained by Luca Persichini.
- Add guided work-item merges, per-file planning, conflict resolution and configurable team/personal policies.
- Rename the extension, assemblies, namespaces, solution and projects to MultiMerge.
- Use distinct Visual Studio package, command and Team Explorer identities.
- Preserve existing team policies and import personal preferences on first use.
- Build and 314 automated tests verified with Visual Studio 2026.

## Original AutoMerge release history

The entries below are retained from the upstream AutoMerge project.

#### 0.2.6.10 (2021-03-19)
* (fix) When you have multiple branches with same prefix, e.g. ABC and ABCDE. When merging to ABC method choses ABCDE instead, and as a result there are multiple branches with the same name in target branch selector

#### 0.2.6.9 (2017-05-09)
* (fix) If the comment doesn't contain any target branch information the comment will be the same for each target branch. Consolidate duplicate comments when displaying in the Pending Changes view (thanks psaut)

#### 0.2.6.8 (2017-03-02)
* (enhancement) Order workspaces by name in "Target branches" list (thanks MrLuje)
* (fix) Fix typo at "Resolve" word

#### 0.2.6.7 (2016-09-02)
* (enhancement) Added the ability to specify the number of changesets shown for merging. Name - changeset_count_show

#### 0.2.6.6 (2016-03-21)
* (enhancement) Add "{SourceWorkItemTitles}" keyword for comment_format (#22)

#### 0.2.6.5 (2015-11-26)
* (fix) Null reference when config does not exists

#### 0.2.6.4 (2015-11-23)
* (fix) Crash when comment template has newline (#18)

#### 0.2.6.3 (2015-05-18)
* (fix) The extension requires a version of .NET Framework that is not installed. (#15)

#### 0.2.6.2 (2015-03-30)
* (fix) SQL Server error 2627

#### 0.2.6.1 (2015-01-19)
* (fix) Showing all changes regardless of the project

#### 0.2.6 (2015-01-18)
* (enhancement) Change behavior "Merge & Check In". Now it check in if no conflicts, otherwise show resolve conflicts window.
* (enhancement) If found to be restored unexpected file, merging occurs by file.
* (enhancement) After check in show changeset id.
* (fix) Fix refreshing changeset when project changed.

#### 0.2.5.1 (2014-12-03)
* (enhancement) Allow templating discard merge comment (#11)

#### 0.2.5 (2014-11-29)
* (enhancement) Allow templating merge comment (#6)

#### 0.2.4 (2014-09-24)
* (enhancement) Allow set default mode for "Merge" button

#### 0.2.3 (2014-09-22)
* (enhancement) Double click on branch open it in source control explorer (#7)
* (fix) Multi load branches info, because not unsubscribe events

#### 0.2.2 (2014-06-23)
* (fix) Button "Merge" not disabled while merging (#3)
* (enhancement) Rename button "Merge" -> "Merge & Check In"
* (enhancement) Save last merge operation and next time it will be default (#4)

#### 0.2.1 (2014-06-13)
* (fix) In some cases occurs "Cannot add instance of type 'ScrollDeligateBehavior' to a collection of type 'BehaviorCollection'" (#2)
* (enhancement) Add support multi workspace (#1)

#### 0.2 (2014-05-20)
* Initial Release
