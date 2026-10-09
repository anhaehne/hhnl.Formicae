# Scope and decisions

Approved by product owner on 2026-10-09: “Go ahead” following the baseline revision proposal. Two prior imports of the approximately 3.5 GiB worker archive exceeded the fixed five-minute deadline. The existing runner discards captured command output when timing out. Diagnose whether import is progressing or stalled; avoid replacing real worker Kubernetes verification with Docker-only probes. Keep loading bounded and configurable and retain useful phase/runtime diagnostics.

No UI changes or visuals. No production workflow/runtime behavior change. Implementation and verification must respect the shared runner quiet window requested by rampant-fly (`ad63c51`).

## Diagnosis and selected repair

The preserved 3.68 GB image imported successfully using the existing `kind load image-archive` interface. Containerd reported all 15 blobs complete and the root filesystem unpacked; `crictl inspecti` resolved the exact worker image. During loading, both `ctr` and containerd used CPU. The shared two-core runner also has competing builds/browser suites. This supports load-sensitive setup deadlines; it does not establish a containerd deadlock. Retain native archive loading, raise the default import bound to 15 minutes, allow 1–60 minutes via `FORMICAE_E2E_IMAGE_LOAD_TIMEOUT_MINUTES`, and retain partial stdout/stderr plus elapsed/limit evidence on timeout. Add per-image phase/size diagnostics, node image/runtime diagnostics, and native CRI verification before deployment.

The real Kubernetes run also exposed a fixture error in the existing output-correction probes: `Capabilities: []` intentionally suppresses all profile tools. Selecting `tool:output-probe` installs the intended CLI probe. Four existing cases were corrected, with worker logs retained before teardown. This preserves the originally approved output-correction coverage and changes no application behavior or deadlines.
