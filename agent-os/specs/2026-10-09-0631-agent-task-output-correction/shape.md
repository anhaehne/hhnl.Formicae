# Scope and decisions

Approved by the product owner on 2026-10-09 with “lgtm” after the baseline proposal. Applies to inline Agent nodes and catalog custom tasks with declared outputs. Free-text tasks retain existing behavior. No UI or database schema changes.

Correction belongs inside the existing ephemeral worker, before job completion. Orchestration restart reattaches to the same attempt/job instead of launching another worker. Kubernetes workers use restartPolicy Never and backoffLimit 0, so a worker failure remains a failed attempt rather than resetting correction progress. Runtime logs and callbacks retain numbered correction messages and all CLI responses under the attempt identity. Native Codex exec resume and OpenHands --resume append correction messages to saved conversations.

Keep existing strict scalar validation and limits. No markdown repair or logs-as-output fallback. A successful CLI process without an authoritative response triggers correction; nonzero exit and cancellation do not. Missing conversation identity fails explicitly because creating a fresh task would repeat work. The bounded loop runs under the original hard deadline.

Visuals: none; this changes execution behavior only. Product alignment: ephemeral Kubernetes agents, pinned workflow context, durable attempt evidence and validated task data.

Native structured-output support was considered. The shared existing scalar validator preserves the application's optional-output rules, duplicate-name rejection and size/number bounds consistently across both CLI providers. Output instructions are idempotent even when pinned persona guidance follows them, so the worker does not grow an already validated prompt with a duplicate contract.
