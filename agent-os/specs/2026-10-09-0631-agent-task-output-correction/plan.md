# Agent and custom task output correction

1. Save spec documentation using approved baseline revision `agent-task-output-correction`.
2. Include the pinned output contract and limits in effective prompts and pass its schema to the worker, retaining old prepared prompts for compatibility.
3. Validate authoritative per-turn completion in the worker and resume the same native CLI conversation up to twice within its original timeout. Retain correction progress in runtime and callback evidence. Publish only validated output; keep orchestration-side validation.
4. Add regression tests for both providers, output extraction, exhausted/missing completion, failed turns, deadline/cancellation, and downstream release. Run targeted tests, repository verification, application smoke and Kubernetes E2E.
5. Align task/deployment documentation and release versions, following the once-per-branch version rule.
