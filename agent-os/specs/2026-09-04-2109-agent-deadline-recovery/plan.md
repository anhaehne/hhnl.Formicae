# Formicae 0.7.5 Agent Deadline Recovery

## Summary

Preserve the blocked 0.8.0 commit on `release/0.8`, safely revert it from `main`, and release a focused 0.7.5 recovery build. Implementation workers receive 60 minutes, begin checkpointing after 50 minutes, and push recoverable work before termination. Tests use fake time and short test-only durations.

## Implementation Changes

1. Save this specification before implementation.
2. Preserve commit `fe64887a0579c2332cb8fe49b4f88fbeeb7bf28f` on `release/0.8`, then revert it without rewriting history.
3. Add an optional runtime execution policy. `Implement` and `AddressComments` receive a 3,600-second deadline and 600-second checkpoint window; lightweight jobs retain the existing 1,800-second default.
4. Make the worker interrupt and resume Codex with a checkpoint instruction at the soft deadline, then deterministically commit and push remaining changes. Checkpointed runs remain failed and retryable and cannot create a pull request.
5. Release the recovery as version 0.7.5 through the existing main-branch deployment pipeline without creating a Git tag or GitHub Release.

## Test and Acceptance Plan

- Use fake time for unit tests and seconds-scale limits for process integration tests. No automated test waits for production timeout values.
- Cover policy selection, Kubernetes and container propagation, normal completion, notification/resume, forced checkpoint, no changes, push failure, secret redaction, and SIGTERM.
- Run the .NET suite, frontend build, Playwright smoke tests, Helm validation, worker-image checks, and Kubernetes E2E suite.
- Confirm the failed 0.8.0 migration is absent from live migration history before deployment.
- Verify 0.7.5 health and generated implementation-job deadline settings, then retry issue #62 through Formicae as the real-agent acceptance test.

## Assumptions

- `release/0.8` preserves the blocked release and is not a deployment source.
- Issue #62 and its EF migration remain outside this specification.
- Checkpoints are pushed only to isolated workflow branches and always leave the run failed and retryable.
- No management UI, database schema, tag, or GitHub Release is added.
