# Verification

## Diagnostic evidence

- Native command: `kind -v 6 load image-archive /tmp/formicae-output-correction/formicae-worker-e2e.tar --name formicae-import-diagnostic`; completed with exit 0.
- Image: `sha256:c76240de22aa31101af715e960b8463692e00179149bcf362da1893634a8355e`, Docker size 3,678,565,208 bytes. The archive was retained from the earlier failed setup.
- `docker exec formicae-import-diagnostic-control-plane ps -eo pid,etime,pcpu,comm`: both `ctr` and containerd active during import.
- `docker exec formicae-import-diagnostic-control-plane ctr --namespace=k8s.io images check`: worker complete (15/15), 3.5 GiB, unpacked true.
- `docker exec formicae-import-diagnostic-control-plane crictl inspecti localhost/hhnl-formicae-worker:e2e`: exit 0, correct image identity and tag.
- Available disk: 227 GB; no disk-full condition observed.

## Coordination

At the shared user’s request, sent status to rampant-fly (`ad63c51`) using the Paseo skill. Preserved the active diagnostic import, then notified the agent when it completed. No builds, suites or new image imports started during the requested frontend/image-acceptance quiet window. Light code/docs work continued. Regression checks await release of that window.

## Test coverage

14 new infrastructure cases cover successful/failed command output, timeout output and process cleanup, caller cancellation, and deadline configuration. Four existing Kubernetes correction cases were edited to select their CLI probe capability and retain worker logs. No cases removed.

## Released-window checks

`dotnet test tests/hhnl.Formicae.KubernetesE2ETests/hhnl.Formicae.KubernetesE2ETests.csproj --no-restore --filter 'FullyQualifiedName~CommandRunnerTests' --verbosity minimal`: final run passed 14/14. The first run had one assertion error: it matched the completion marker inside the echoed command rather than captured output. The corrected assertion checks output lines, and the rerun passed.

`FORMICAE_E2E_CLUSTER_NAME=formicae-import-repair ./scripts/run-k8s-e2e.sh`: image build/import, CRI verification and API deployment succeeded; 28/32 cases passed in 16 minutes 14 seconds. Four native correction cases failed because their explicit empty capability list disabled installation of the intended CLI probe. The test setup now explicitly selects `tool:output-probe`; production code and runtime deadlines are unchanged. The fixture automatically removed its owned cluster.

The rebuilt worker verified in CRI was `sha256:0af21e509dc2d1df78fcad5dded92553b19a152593188f73fc087c03ab5742cc`, size 3,736,438,408 bytes. The full run progressed beyond the formerly blocked import step and exercised real native worker/Kubernetes behavior.

`helm lint deploy/helm/formicae` and `git diff --check` passed. No frontend or production application source changed in this repair.

## Final correction rerun and cleanup

```sh
FORMICAE_E2E_CLUSTER_NAME=formicae-import-diagnostic FORMICAE_E2E_WORKER_IMAGE=localhost/hhnl-formicae-worker:e2e dotnet test tests/hhnl.Formicae.KubernetesE2ETests/hhnl.Formicae.KubernetesE2ETests.csproj --no-restore --filter 'FullyQualifiedName~Native_worker_corrects_output_in_same_conversation_and_enforces_limit' --logger 'trx;LogFileName=output-correction-after-import-repair.trx' --results-directory test-results/kubernetes-import-repair --verbosity minimal
```

Passed 4/4 in 17 seconds after setup, covering OpenHands and Codex correction success and exhaustion in real Kubernetes worker Jobs. The built worker image was reused; no production code or task deadlines changed. This completes passing evidence for all 32 cases across the initial full run (28 passing) and the corrected focused rerun (4 passing); it is not a claim that the initial full run was clean.

Artifacts are under ignored `test-results/kubernetes-import-repair/`: initial-suite.log, correction-rerun.log, output-correction-after-import-repair.trx, and worker-results/*.log.

`kind delete cluster --name formicae-import-diagnostic --kubeconfig /tmp/formicae-import-diagnostic.kubeconfig` removed the manually owned diagnostic cluster. The initial suite had already removed its own formicae-import-repair cluster. `kind get clusters` returned none; `docker ps --format '{{.Names}}'` returned no containers. Final `git diff --check` passed.

The exclusive CPU/runtime window was explicitly released to both coordinating agents after all checks and cleanup. No new build/test/import work is pending. This change affects test infrastructure and documentation; production release versions remain 0.26.0.
