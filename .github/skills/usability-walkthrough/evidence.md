# Walkthrough evidence and publication checklist

Read with [SKILL.md](SKILL.md). This is a content contract, not fabricated
findings or a prefilled report. Keep private audit evidence in session storage;
public reports/issues/PRs must contain only safe synthetic data and relevant
public source provenance.

## One offline HTML report

Use one responsive, self-contained HTML file with a title, concise outcome,
section navigation and accessible tables/details. Do not commit reports,
screenshots, private paths or session assets to Git. Inline only sanitized
synthetic images/SVG/data URIs. No remote assets/scripts/fonts or required
network calls. Actual issue/PR/source links may be ordinary optional links;
the report must remain readable offline.

Required sections:

1. **Run and provenance:** mode, time, actual OS/runtime/browser, invoking
   checkout versus reviewed checkout, full reviewed HEAD/tree/default-base
   SHAs, meaningful local diff fingerprints, tested asset/executable provenance.
   State classification and evidence/rationale. Do not expose a private
   baseline patch; use safe summaries/fingerprints.
2. **Coverage inventory:** every discovered existing button/input/control,
   scene/flow, conditional state and keyboard/focus/scroll/error transition.
   Columns: control/flow ID, source at SHA/line, exercised states, evidence,
   runtime/native-synthetic/source-only/blocked coverage and limitation.
   A synthetic placeholder image is not a screenshot of real runtime.
3. **Baseline findings:** stable finding ID, accepted/rejected/blocked,
   evidence type (reproduced functional defect, observed layout issue, informed
   expert judgement or untested hypothesis), confidence/rationale,
   priority/concrete user cost, setup/steps, expected versus actual,
   source provenance and functional repro or baseline layout evidence.
   State which published usability principle applies without claiming a real
   person's review. Explain rejected ideas and blocked suspicions concisely.
4. **Layout and density assessment (required even with no functional bugs):**
   KEEP / necessary CHANGE / DEFER for each major UI area. Cover overall
   information hierarchy, busy collapsed/expanded PRs, repeated metadata/chrome,
   spacing/clipping, action labels/grouping/primary-secondary-danger hierarchy,
   state-dependent actions, visibility versus hide/expand, supported Windows
   sizes/scaling and discoverability/extra-click tradeoffs. For each assessment
   give source/render evidence at the reviewed snapshot (or explicitly missing),
   concrete user cost or benefit of keeping the current arrangement, minimal
   proposal, observable acceptance/demonstration, tradeoffs and confidence.
   Preserve blockers, running status, Stop/cancel and original one-click
   PoP/auto-complete/triage access. Do not prescribe hiding everything in More.
   Distinguish observation from expert interpretation and untested hypothesis;
   no invented human study, measured speed gain or universal optimum.
   Recommendations are not automatically selected for implementation.
5. **Selected fix plan:** each accepted necessary improvement's smallest fix,
   direct tests/docs, exact destination (isolated main PR or current worktree/current PR),
   preserved prior work, real dependencies, deduplication outcome, numbered
   acceptance criteria and required closure demonstration. Explicitly say
   what will and will not be fixed in the invoking worktree.
6. **Corrected results:** per-fix reviewed base and actual fix HEAD/tree,
   criterion-by-criterion result/evidence, commands with results, synthetic
   versus live boundaries, owner versus independent verification, limitations
   and issue/PR status/URLs. Preserve baseline failures, do not rewrite them
   as if they never happened. "Not run" is not "passed".
7. **Delivery status:** committed/pushed/PR created or updated, existing
   deduplicated work, failed/uncertain publication and pending prerequisites.
   If zero accepted necessary findings, say report-only, retaining layout advice.
   No empty issue/PR. Zero functional bugs alone does not cancel layout selection.

## Main-mode issue body

Follow a fitting repository issue template if one exists; put this content
inside its sections without removing headings, comments or required fields.
Otherwise use these concise headings:

```markdown
## Usability finding and impact
<UX-ID, functional/layout evidence type, affected existing control/state,
priority, concrete user cost and confidence/rationale>

## Source and baseline demonstration
<reviewed full SHA/tree, source path/lines, actual platform/runtime;
synthetic fixture/interception boundary, rendered state/window/scaling
and evidence provenance>

Preconditions: <reproducible safe setup>
1. <observable action>
2. <observable failure or baseline layout concern>

Expected: <specific observable behavior>
Actual: <reproduced behavior or observed layout; separate expert interpretation>

## Scope and dependencies
<accepted necessary minimal improvement and direct tests/docs;
discovery/extra-click/status/action tradeoffs, exclusions and actual prerequisites>

## Acceptance criteria
1. <observable behavior or repeatable before/after layout improvement>
2. <necessary recovery/keyboard/scroll/stale-state criterion if applicable>

## Closure evidence required
<exact targeted test command and expected assertion/output, plus exact
manual/synthetic demonstration required for every numbered criterion;
platform needed and any native/tenant check still required>

## Data and authorization boundaries
<synthetic/public data only; no credentials, private org/project/repo/PR
details; no real model/Speech/approval/PoP/ADO writes without separate consent>
```

Criteria must be measurable behaviors, not "improve UX", "add tests", a desired
test count or a claim to verify unexercised tenant behavior. Do not require
irrelevant E2E just to fill a section. If native behavior is the defect, source
inspection or a browser mock alone cannot satisfy its closure criterion.
An observed layout issue does not need an exception/crash reproducer, but an
issue still requires accepted necessary scope, concrete user cost, actual
source/render evidence and a repeatable observable improvement. For layout
criteria use the same states/window/scaling before and after, checking critical
actions/status and keyboard/focus/discoverability remain usable. Expert advice
or a DEFER recommendation alone is not an issue or implementation mandate.

## Every fix PR: preserve the repository template

Keep the template headings, HTML comments, checklist items and order. Add the
walkthrough evidence within its Summary/Testing sections; do not replace the
template with the issue skeleton. Preserve existing text in current-branch mode.
Do not check the privacy checkbox without actually reviewing outgoing content.

Include finding/issue mapping, narrow fix, actual dependencies, full PR head and
reviewed baseline SHAs, platform/fixture provenance, report reference and:

| Criterion | Concrete result | Evidence/command or manual demonstration | Boundary/status |
| --- | --- | --- | --- |
| `<UX-ID AC1>` | `<actual observed behavior>` | `<exact command + passed/failed result, or numbered manual steps + observation>` | `<owner synthetic, owner native synthetic, independent re-audit, live E2E, or blocked>` |

One row for **each** numbered criterion. Include exact relevant test commands
with their results, skipped/unavailable checks and why. If the PR head changes,
recheck affected criteria and update the SHA/evidence before asserting closure.
An old head's results cannot be relabeled with a new head.

Use `Fixes`/`Closes` only after all required criteria are evidenced, without
manually closing or merging anything. Otherwise use `Refs` and list precisely
what remains unproven. Keep native-synthetic PoP lifecycle separate from real
tenant authentication, fake video generation separate from real decoding, and
fake host action success separate from real ADO compatibility.

In **current-branch** mode, no issue is created. Put the same numbered
`UX-ID ACn` checklist and evidence table in the report and existing PR, while
retaining its prior issue mappings and evidence. Do not attach new closure
keywords to unrelated existing issues.

## Publication/retry ledger

Record each ID's deduplication query/result, issue/PR canonical URLs and live
state, local commit SHA, pushed head, last successful step and blocker in the
private session audit; sanitize when included in the report. After a timeout or
uncertain API outcome, read live state before retrying creation or edits.
Never silently choose another account, manufacture a URL, downgrade a failed
write to success, overwrite concurrent PR text or duplicate records.
