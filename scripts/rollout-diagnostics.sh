#!/usr/bin/env bash
# Best effort: diagnostic failures must never replace the original rollout failure.
set -u
namespace="${RELEASE_NAMESPACE:-formicae}"
release="${RELEASE_NAME:-formicae}"
selector="app.kubernetes.io/instance=${release},app.kubernetes.io/component=api"

kubectl get deployments,replicasets,pods -n "$namespace" -o wide || true
kubectl describe deployment "${release}-api" -n "$namespace" || true
kubectl describe pods -n "$namespace" -l "$selector" || true
kubectl get events -n "$namespace" --sort-by=.metadata.creationTimestamp || true

# Enumerate pods instead of deployment logs, which can omit failing replicas.
pods="$(kubectl get pods -n "$namespace" -l "$selector" -o name)" || pods=""
for pod in $pods; do
  containers="$(kubectl get "$pod" -n "$namespace" -o jsonpath='{.spec.initContainers[*].name} {.spec.containers[*].name}')" || containers=""
  for container in $containers; do
    echo "=== ${pod}/${container}: current logs ==="
    kubectl logs "$pod" -n "$namespace" -c "$container" --tail=200 || true
    echo "=== ${pod}/${container}: previous logs (if available) ==="
    kubectl logs "$pod" -n "$namespace" -c "$container" --previous --tail=200 || true
  done
done
exit 0
