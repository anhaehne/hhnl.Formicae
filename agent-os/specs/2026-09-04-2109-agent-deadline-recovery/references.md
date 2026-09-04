# References for Agent Deadline Recovery

## Existing Implementations

- `src/hhnl.Formicae.Infrastructure/Kubernetes/KubernetesJobRunner.cs` — current global timeout, Kubernetes active deadline, and job result/log handling.
- `src/hhnl.Formicae.Infrastructure/Containers/ContainerJobRuntime.cs` — container timeout behavior that must honor the same per-job policy.
- `src/hhnl.Formicae.Infrastructure/OpenHands/OpenHandsAgentRunner.cs` — task-kind capability selection and runtime job creation.
- `src/hhnl.Formicae.Worker/Program.cs` — Codex execution followed by worker-owned commit and push.
- `.github/workflows/deploy-formicae.yml` — main-only deployment chain and live rollout behavior.
- GitHub issue #62 and workflow `74b27214f28b4e7f97196bc493a080d6` — evidence from the timed-out implementation attempt.

## External Reference

- OpenAI Codex CLI reference: non-interactive `codex exec resume` accepts a session ID and follow-up prompt.
