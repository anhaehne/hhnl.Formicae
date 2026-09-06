# References
- GitHub issue https://github.com/anhaehne/hhnl.Formicae/issues/10
- WorkflowOrchestrator.cs: single cursor, task launch and completion, loop normalization.
- WorkflowNodeDefinitions.cs: control-node compilation and validation.
- WorkflowService.cs: retries.
- WorkflowOrchestrationLocks.cs: serialized ticks.
- OpenHandsAgentRunner.cs and KubernetesJobRunner.cs: external job identity and AlreadyExists handling.
- Existing workflowEditor modules and node measurement regression tests.
- Independent reviewer: parallel_plan_review; required restart/input/retry/idempotency clarifications incorporated in plan.
