# Workflow template node — shaping notes

Scope: user-requested textarea template, any number of connected data inputs and rendered text output.

Decisions: use Scriban, an existing .NET package; input identifiers live under `inputs` to avoid collisions with builtins. Reuse scalar types and existing producer/provenance checks. Inputs are required connections; no artificial input-count limit. Templates run inline without workers. Preserve output contract limits and freeze resolved input evidence for retries.

Context: existing node cards, inspector and data connections are the visual references. No supplied mockups. This extends customizable workflows without changing mission or stack.
