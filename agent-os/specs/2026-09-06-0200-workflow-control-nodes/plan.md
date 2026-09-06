# Loops and triggers as workflow nodes

1. Save shaping documentation.
2. Add v1alpha3 task, trigger, and loop nodes with individual settings. Normalize them to the existing execution plan, preserving historical versions and active runs.
3. Route trigger starts through their own output. Derive fixed-count loop bodies from Body/Return connections and exits from Exit connections; retain iteration guards, retries and restart recovery.
4. Replace configuration lists with Add Step node choices and selected-node settings using native React Flow handles. Convert legacy definitions in the editor only; save as a new immutable version.
5. Validate graph structure, compatibility, trigger entry routing, deduplication, loops, retry/restart, browser editing and Kubernetes behavior. Report commands and test counts.

No nested loops, conditional loops, event waits, parallelism, or new trigger types. Manual starts retain an explicit task/loop entry. Use a single minor version bump. No deployment is included.
