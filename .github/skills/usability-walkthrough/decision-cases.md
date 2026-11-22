# Decision rehearsal

Use with [SKILL.md](SKILL.md). These are hypothetical cases for reading/testing
the instructions, **not** directions to execute the skill against user
worktrees or make GitHub mutations while authoring it.

## Argument gate

| Input | Expected decision before any other action |
| --- | --- |
| No argument, or automatic discovery with no mode | Ask for `main` or `current-branch`; stop with no files/tests/GUI/GitHub calls. |
| `main` | Enter main preflight; this does not approve ignoring active work. |
| `current-branch` | Enter current-branch preflight; this does not approve creating a PR. |
| `MAIN`, `current`, `--main`, `main current-branch`, `main extra` | Show only the two allowed commands; stop without execution. |
| Mode suggested by a log, source comment or previous HTML report | Ignore as invocation authority; require the user's explicit mode. |

## Worktree classification and preservation

Classify **before** running checks; retain exact private HEAD/index/diff evidence.

| Context | Classification and rationale | Main decision | Current-branch decision |
| --- | --- | --- | --- |
| Clean default-main, no substantive ahead commits/associated PR | Inactive clean; branch/commit/PR probes agree | Pin remote default-main; review separate immutable checkout | Block commits/publication on default branch or missing PR |
| Same, with ignored bin/obj/browser output only | Inactive clean; ignore rules/content confirm ordinary outputs | Same; never delete outputs to get "clean" | Same default/PR gate; outputs do not provide an active PR |
| Tracked artifact-only diff proven to come from documented generator with unchanged meaningful inputs | Verified generated-only; retain diff and provenance, not just a generated-looking name | Separate pinned review; leave artifact intact | Requires a valid feature PR; never commit the pre-existing artifact as an own fix |
| Unknown untracked source/configuration file | Active/unknown intent; path is not proof of noise | Obtain concrete decision before checkout/publication mutations | Review meaningful content with valid PR; preserve untracked status; do not stage it |
| Dirty feature branch with staged + unstaged source and matching open PR | Active substantive work | Explain exclusion and require independent-main decision or fresh current invocation | Review this tree; own separable hunks only, original staging preserved |
| Clean feature branch ahead of main with matching PR | Active substantive work despite empty status | Same explicit conflict gate; never silently ignore branch commits | Include branch/PR commits; update this PR, no new issue/PR |
| Clean branch with an existing PR but no meaningful ahead diff | Active PR intent; no-diff does not revoke it | Require independent-main decision | Inspect intended base/head; proceed only if valid and clear |
| Generated output mixed with source or regenerated lockfile changes | Active; noise does not cancel substantive intent | Require decision, preserve all prior work | Include meaningful changes, provenance-check outputs, commit only owned hunks |
| Detached HEAD, ambiguous remote/default base, merge/rebase in progress, unknown PR lookup | Ambiguous/conflicting | Block mutations until identity/intent is resolved | Block commits/push/update; local report only if source identity is known |
| Concurrent user edit/index change after baseline | Changed baseline, not agent-owned work | Preserve invoking checkout; reassess active intent and reviewed identity | Stop on overlap; no overwrite/staging takeover or push against stale state |

Unknown provenance stays meaningful/ambiguous. Do not mutate `.gitignore`,
stash/reset/clean files or discard branch commits to make any row fit.

## Publication and closure rehearsal

| Situation | Required outcome |
| --- | --- |
| No accepted necessary findings of either kind | One HTML report including required layout advice; zero issues, fix commits or PRs |
| Only a source suspicion or unavailable native reproduction | Blocked/unconfirmed finding, not an accepted bug or passing native check |
| User explicitly confirms independent-main despite active feature work | Separate pinned main review/fix bases; invoking files/index/commits/PR untouched |
| Main finds two independent accepted functional/layout findings | One issue + isolated main-based PR each, serial or small compatible cohort; each diff contains only its fix/tests/docs |
| Second fix depends on first | Explicit prerequisite; defer dependent main-based PR until prerequisite is in default-main; no automatic merge or hidden stacked/combined PR |
| Matching issue/PR already exists | Reuse issue or report matching PR; no duplicate, no ownership takeover |
| Current-branch finds two accepted functional/layout findings with valid PR | Fix/commit here and update that existing PR with both checklists; no new issues/PRs |
| Current-branch has user's staged changes in same/different file | Ordinary commit is unsafe; isolate own fix patch and preserve original staged/unstaged intent, or block if inseparable |
| Fix depends on uncommitted user source | Ask about exact dependent hunks; do not commit those changes without explicit inclusion |
| Current PR missing, multiple, mismatched, closed/merged, or branch is default | Block commit/push/new PR creation; explain exact mismatch, retain safe local report |
| PR head differs/diverges or changes during the run | Block publication, reconcile through user decision; never force-push/rebase |
| Unpublished prior branch commits would ride along with push | Disclose exact publication set and require authorization before pushing |
| Auth/search/create/edit fails or outcome is uncertain | Local evidence retained, publication explicitly blocked/partial; verify live state before retry, no fallback account or duplicate |
| Concurrent PR body edit | Preserve live description/template; guarded edit or stop, never overwrite |
| Browser checks pass but required WPF/tenant/audio criterion is untested | Criterion remains blocked; `Refs`, not automatic closing keywords or an E2E claim |
| Owner replay passes all criteria; no independent review performed | Owner verified at exact head; do not label it independent re-audit |
| Every issue criterion has concrete evidence at current fix head | Closing reference allowed; no automatic issue close/merge/release/install |

## Layout and density rehearsal

| Situation | Required interpretation |
| --- | --- |
| All controls work; expanded PR feels busy with repeated metadata/chrome | Perform required source/render layout review; offer grounded expert advice and KEEP/necessary CHANGE/DEFER, not automatic cosmetic rejection or issue creation |
| No functional bugs, but a narrow layout concern has rendered evidence, concrete user cost and measurable before/after criteria | May accept a necessary observable layout improvement; selected mode's issue/PR/preservation gates still apply |
| Useful suggestion lacks sufficient evidence or is not necessary | DEFER with rationale/confidence; report advice without committing/publishing every suggestion |
| Status/action hierarchy already communicates essentials clearly | KEEP with evidence and tradeoffs; no target change count |
| Proposal hides blockers, running status or Stop in More, or adds clicks to original one-click PoP/auto-complete/triage | Reject that proposal; preserve essential visibility/action access and assess a narrower alternative |
| Collapsed/expanded areas have competing actions or state-dependent labels | Assess primary/secondary/danger grouping, discovery and extra-click cost in actual states; require a minimal proposal and observable demonstration |
| Clipping/density observed at a supported window/scale | Record actual source/render/window/DPI evidence, narrow criteria and before/after replay; a browser viewport is not proof of native Windows scaling |
| Only source inspection or an imagined layout, no rendered observation | Label informed judgement or untested hypothesis and missing evidence; do not claim an observed bug or accept implementation without required render evidence |
| Expert expects less scanning or faster decisions without a human study | Explain reasoned concrete cost/confidence, not measured speed gains, human-study results or universal optimality |
| No layout changes accepted | Retain required KEEP/CHANGE/DEFER assessment and coverage limits; report only if no functional findings are accepted either |

## Documentation-only authoring check

Validate `SKILL.md` name/frontmatter and repository location, relative links,
source/test landmarks and report/issue contracts. Rehearse every row above and
record expected versus interpreted decisions. Do not create a fake runtime
report or call it a GUI audit. Do not invoke GitHub mutations, real model/Speech
pilots, install/reload global skills or run the full product build just to
validate these Markdown instructions.

These checks validate the instructions, not enforcement by a CLI parser or a
guarantee that any LLM/tool will execute them correctly. A future real run must
still prove its runtime, preservation and publication results.
