# Kubernetes E2E image import repair

1. Save spec documentation for approved baseline revision `kubernetes-e2e-image-import`.
2. Reproduce the real native worker import and capture process/runtime evidence before choosing a fix.
3. Repair bounded image loading and retained command diagnostics using native kind/container interfaces. Preserve Docker/Podman support and isolated cluster cleanup.
4. Add focused regression coverage and run targeted/fast checks, then the actual Kubernetes suite in a coordinated quiet CPU window.
5. Document exact evidence, commands, outcomes and version implications.
