$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$patterns = @(
    "C:\\Users\\",
    "ALX",
    "CodexProjects",
    "PowerRecover_PRO_REVIEWED",
    "PowerRecover_PRO_ASSISTANT",
    "api[_-]?key",
    "secret",
    "github_pat",
    "ghp_",
    "sk-[A-Za-z0-9]{20,}"
)

$paths = @(
    "PowerRecover.App",
    "PowerRecover.Cli",
    "PowerRecover.Engine",
    "PowerRecover.Tests",
    "BootableUsb",
    "TestRecoveryKit",
    "docs",
    "scripts",
    "README.md",
    "CONTRIBUTING.md",
    "SECURITY.md"
)

Push-Location $repoRoot
try {
    foreach ($pattern in $patterns) {
        Write-Host "Checking: $pattern"
        rg -n --glob "!**/bin/**" --glob "!**/obj/**" --glob "!release/**" --glob "!scripts/audit-public.ps1" $pattern $paths
    }
}
finally {
    Pop-Location
}
