[CmdletBinding()]
param(
    [ValidateSet("docker", "podman")]
    [string]$ContainerCli = $(if ($env:FORMICAE_CONTAINER_CLI) { $env:FORMICAE_CONTAINER_CLI } else { "docker" }),
    [switch]$KeepCluster
)

$ErrorActionPreference = "Stop"
$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$testProject = Join-Path $repoRoot "tests/hhnl.Formicae.KubernetesE2ETests/hhnl.Formicae.KubernetesE2ETests.csproj"

function Assert-Tool {
    param(
        [Parameter(Mandatory)] [string]$Name,
        [Parameter(Mandatory)] [string[]]$Arguments,
        [Parameter(Mandatory)] [string]$InstallHint
    )

    $command = Get-Command $Name -ErrorAction SilentlyContinue
    if (-not $command) {
        throw "Required tool '$Name' was not found on PATH. $InstallHint"
    }

    & $Name @Arguments | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw "Required tool '$Name' failed preflight. $InstallHint"
    }
}

Assert-Tool -Name "kind" -Arguments @("version") -InstallHint "Install kind before running Kubernetes E2E tests."
Assert-Tool -Name "kubectl" -Arguments @("version", "--client") -InstallHint "Install kubectl before running Kubernetes E2E tests."
Assert-Tool -Name $ContainerCli -Arguments @("--version") -InstallHint "Install $ContainerCli or pass -ContainerCli docker|podman."

$env:FORMICAE_CONTAINER_CLI = $ContainerCli
if ($ContainerCli -eq "podman") {
    $env:KIND_EXPERIMENTAL_PROVIDER = "podman"
}

if ($KeepCluster) {
    $env:FORMICAE_E2E_KEEP_CLUSTER = "true"
}

Write-Host "Running Kubernetes E2E tests with $ContainerCli. The tests use a temp kubeconfig and do not modify the default kubectl context."
dotnet test $testProject
exit $LASTEXITCODE
