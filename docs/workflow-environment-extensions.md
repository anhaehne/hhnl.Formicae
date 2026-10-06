# Workflow and environment extensions

Version 0.20.0 implements script steps, per-step capabilities and secret references, native MCP integration, custom execution images and ordered tool installation. Environment configurations stay at schema version 1. Saved workflow and environment versions retain their immutable settings; running and historical execution graphs show the pinned settings and actual script exit codes.

## Scripts

Add a Script node in the workflow editor. Choose `sh` or `bash`, enter the script, choose `workspace` or `repository` working directory, and set a timeout of 1–3600 seconds. Workspace scripts need no AI configuration or repository checkout. Repository scripts use the workflow's connected repository and branch. Scripts do not automatically commit or push changes.

Scripts share durable attempts, retry, pause, cancellation and visual investigation with agent steps. Stdout and stderr appear in live and historical task logs. Exit code zero succeeds; nonzero fails with the actual exit code; the worker deadline reports 124. Installation and checkout consume the same overall task budget. Kubernetes allows 30 seconds beyond the worker deadline for startup and final reporting.

A Script node exposes one named string output, `output`, containing sanitized stdout, up to 256 KiB with an explicit truncation marker. Connect it to a custom-task input using the editor's binding controls. Consumer input validation still applies: values exceeding the 16,000-character input limit fail validation rather than silently changing the value. Stderr remains diagnostic evidence.

## Capabilities and secrets

The execution inspector supports inherited capabilities or an explicit list. Available provisioning capabilities are `browser`, `nested-containers`, `mcp:<server-name>` and `tool:<tool-name>`. An explicit empty list enables none. Agent implementation/address-comments steps inherit browser and nested-container provisioning; agent steps inherit configured tools and MCP servers. Scripts inherit tools only and reject agent-only capabilities.

Capabilities control Formicae provisioning; they do not sandbox arbitrary shell commands, filesystem access or networking. Kubernetes provisions the Docker sidecar only when nested containers are selected. Docker/Podman execution rejects nested-container requirements with a clear error; use Kubernetes for that capability. Named image-pull Secrets also require Kubernetes.

Secrets are references to an operator-managed Secret name and key, exposed to the worker under a chosen environment alias. Kubernetes validates each selected key before creating the Job, injects only selected keys through native `secretKeyRef`, and preserves external Secrets after completion. For Docker/Podman, operators supply the corresponding `Containers:StepSecrets:<secret-name>:<key>` configuration; values are passed through the container CLI's private child-process environment, never its argument list.

Aliases must be valid environment names and must not collide with reserved worker, model, shell-loader or process-control variables. Configuration APIs, environment history and task evidence contain references only. Known selected values and common encoded forms are masked in worker logs and script output. This masking is a diagnostic safeguard, not a security boundary against deliberately obfuscated output from arbitrary code.

## Environment editor

Open Environments to configure the following fields and save a new version:

- **Image:** compatible execution image reference, `IfNotPresent`/`Always`/`Never` pull policy and optional Kubernetes image-pull Secret names. Extend `docker.io/limeray/hhnl-formicae-worker:0.20.0` to retain the .NET worker and native harness protocol.
- **Tools:** unique name, installation script, `sh`/`bash` shell and per-tool timeout. Enabled installs execute in order before checkout or harness launch. Installation messages stream live; a failed or timed-out install prevents task execution.
- **MCP:** uniquely named stdio servers with command/arguments or HTTP servers with URL. Stdio environment mappings, HTTP headers and bearer authentication refer to selected step-secret aliases. The name `playwright` is reserved for native browser provisioning.

MCP environment mappings specify server-variable-to-step-secret-alias pairs, never raw credentials. HTTP URLs reject embedded credentials and query parameters. Only servers allowed by the resolved step capabilities enter the worker configuration. Missing required aliases fail before harness execution.

Codex receives a fresh private `CODEX_HOME` with selected authentication and generated native TOML. Its project trust override disables repository-local MCP configuration, including on resume. OpenHands receives private native `mcp.json` under an isolated `OPENHANDS_PERSISTENCE_DIR`. Credential-bearing files use mode 0600 and their directories use mode 0700 on Linux. Other harness protocols are not adapters in this release.

## Example

This environment installs a tool and defines a stdio MCP server. The workflow agent step must select `tool:jq` and `mcp:issues` (or inherit them) and reference the key as `ISSUES_TOKEN`.

```json
{
  "schemaVersion": 1,
  "image": {
    "reference": "docker.io/limeray/hhnl-formicae-worker:0.20.0",
    "pullPolicy": "IfNotPresent",
    "pullSecretNames": []
  },
  "tools": [
    { "name": "jq", "script": "command -v jq || (apt-get update && apt-get install -y jq)", "shell": "sh", "timeoutSeconds": 300 }
  ],
  "mcpServers": [
    { "name": "issues", "transport": "stdio", "command": "issues-mcp", "arguments": [], "environmentVariables": { "API_TOKEN": "ISSUES_TOKEN" } }
  ]
}
```

```json
{
  "id": "check",
  "uses": "builtins.script",
  "script": { "script": "printf 'validated\\n'", "shell": "sh", "timeoutSeconds": 60, "workingDirectory": "workspace" },
  "capabilities": [],
  "secretReferences": []
}
```
