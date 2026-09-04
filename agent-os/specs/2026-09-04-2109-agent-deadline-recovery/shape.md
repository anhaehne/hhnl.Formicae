# Agent Deadline Recovery — Shaping Notes

## Scope

Create a bootstrap release that gives commit-capable Formicae jobs more execution time and preserves their work before Kubernetes terminates them. Preserve the blocked 0.8.0 commit while restoring the deployable 0.7.x line.

## Decisions

- Implement the bootstrap recovery manually, then let Formicae retry issue #62 itself.
- Preserve 0.8.0 on `release/0.8` and revert it from `main` without rewriting published history.
- Use a 60-minute hard deadline and a 10-minute checkpoint window for `Implement` and `AddressComments` only.
- Resume the interrupted Codex session with a final checkpoint instruction, with deterministic worker-owned commit and push as the fallback.
- Treat a checkpointed run as failed and retryable; never create a pull request automatically.
- Release version 0.7.5 through the existing deployment pipeline without a Git tag or GitHub Release.
- Automated tests use fake time or seconds-scale limits and never wait for production deadlines.

## Context

- **Visuals:** None.
- **Product alignment:** Kubernetes workers remain ephemeral while workflow state and recoverable repository changes survive termination.
- **Migration scope:** The 0.8.0 migration repair stays in issue #62 and is not implemented here.

## Standards Applied

No current repository standard applies to this timeout-only recovery. The EF migration standard applies to issue #62 separately.
