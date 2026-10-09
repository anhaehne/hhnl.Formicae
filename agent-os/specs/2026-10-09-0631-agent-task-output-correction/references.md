# References

- Application/Workflows/CustomTaskDefinitions.cs: pinned preparation, prompt instructions and strict scalar output parser.
- Application/Workflows/WorkflowOrchestrator.Custom.cs: shared Agent/custom launch and downstream output validation.
- Infrastructure/OpenHands/OpenHandsAgentRunner.cs: runtime-log envelopes and authoritative result extraction.
- Worker/Program.cs: scratch tasks, process output observer, native Codex resume and hard timeout.
- https://developers.openai.com/codex/cli/reference: native exec resume flags; existing repository code already resumes explicit threads.
- https://docs.openhands.dev/openhands/usage/cli/resume: saved conversation identity and native resume.
- https://github.com/OpenHands/OpenHands-CLI/blob/main/openhands_cli/entrypoint.py: headless --resume accepts queued task input and prints Conversation ID.
- tests/hhnl.Formicae.Tests/CustomTaskWorkerTests.cs and CustomTaskOrchestratorTests.cs: timeout, attempt identity and producer-consumer validation.
