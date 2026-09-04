#!/usr/bin/env bash

set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
REPOSITORY_ROOT="$(cd -- "$SCRIPT_DIR/.." && pwd)"
TEST_PROJECT="$REPOSITORY_ROOT/tests/hhnl.Formicae.KubernetesE2ETests/hhnl.Formicae.KubernetesE2ETests.csproj"
CONTAINER_CLI="${FORMICAE_CONTAINER_CLI:-docker}"

require_tool() {
  local tool="$1"
  local hint="$2"
  command -v "$tool" >/dev/null 2>&1 || {
    echo "Required tool '$tool' was not found. $hint" >&2
    exit 1
  }
}

if [[ "$CONTAINER_CLI" != "docker" && "$CONTAINER_CLI" != "podman" ]]; then
  echo "FORMICAE_CONTAINER_CLI must be either 'docker' or 'podman'." >&2
  exit 2
fi

require_tool dotnet "Install the .NET SDK before running Kubernetes E2E tests."
require_tool kind "Install kind before running Kubernetes E2E tests."
require_tool kubectl "Install kubectl before running Kubernetes E2E tests."
require_tool "$CONTAINER_CLI" "Install $CONTAINER_CLI or set FORMICAE_CONTAINER_CLI=docker|podman."

kind version
kubectl version --client
"$CONTAINER_CLI" --version

export FORMICAE_CONTAINER_CLI="$CONTAINER_CLI"
if [[ "$CONTAINER_CLI" == "podman" ]]; then
  export KIND_EXPERIMENTAL_PROVIDER=podman
fi

echo "Running Kubernetes E2E tests with $CONTAINER_CLI. The suite uses a temporary kubeconfig and does not modify the default kubectl context."
dotnet test "$TEST_PROJECT"
