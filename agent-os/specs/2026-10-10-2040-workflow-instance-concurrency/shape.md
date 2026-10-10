# Workflow instance concurrency

User request: limit actively working workflow instances globally and per definition; starts and reactivations enqueue.

Nullable positive limits preserve unlimited defaults. Definition limits are mutable operational settings shared by every saved version. FIFO applies to eligible instances; full definition limits do not block unrelated definitions. Active parallel branches count once. Waiting-only instances release their slot. Paused workers and pending cleanup still count until stopped. Lowering limits prevents new admission without killing current workers. PostgreSQL distributed orchestration locking serializes admission, controls and event matching across replicas. Durable admission and enqueue timestamps survive restart. No new broker/package is needed: use existing EF Core storage and distributed lock.

No visuals provided. Product direction and technologies are unchanged.
