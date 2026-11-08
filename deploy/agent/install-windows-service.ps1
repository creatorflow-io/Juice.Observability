<#
.SYNOPSIS
  Installs Juice Agent under its launcher as a Windows service, "JuiceAgent". Run as Administrator.

.DESCRIPTION
  Expects the published agent and launcher:
    dotnet publish src/Juice.Observability.Agent -c Release -o <InstallDirectory>\agent
    dotnet publish src/Juice.Observability.Agent.Launcher -c Release -o <InstallDirectory>\launcher
  and this host's agent.json (allowlist, Agent:PlatformUrl, Agent:PlatformCaFile, Agent:ReleaseKeyFiles, Agent:EnrollmentTokenFile).

  The service runs as its own virtual account, NT SERVICE\JuiceAgent, with a data directory only that account and
  Administrators can read: the agent's key and identity, and the launcher's installed versions. The service manager
  restarts it when it fails (the launcher exits with the agent's code), after 5 seconds, then 30, then every minute.

.EXAMPLE
  .\install-windows-service.ps1 -InstallDirectory 'C:\Program Files\Juice Agent' -ConfigFile 'C:\ProgramData\Juice Agent\agent.json'
#>
[CmdletBinding(SupportsShouldProcess)]
param(
  [Parameter(Mandatory)] [string] $InstallDirectory,
  [Parameter(Mandatory)] [string] $ConfigFile,
  [string] $DataDirectory = (Join-Path $env:ProgramData 'Juice Agent\data'),
  [string] $Dotnet = (Get-Command dotnet -ErrorAction Stop).Source
)
$ErrorActionPreference = 'Stop'
$name = 'JuiceAgent'
$account = "NT SERVICE\$name"

foreach ($path in @((Join-Path $InstallDirectory 'launcher\juice-agent-launcher.dll'), (Join-Path $InstallDirectory 'agent\manifest.json'), $ConfigFile)) {
  if (-not (Test-Path $path)) { throw "$path is missing" }
}
if (Get-Service $name -ErrorAction SilentlyContinue) { throw "The service $name exists: remove it first with 'sc.exe delete $name'" }

$home_ = Join-Path (Split-Path $DataDirectory -Parent) 'home'
$command = "`"$Dotnet`" `"$(Join-Path $InstallDirectory 'launcher\juice-agent-launcher.dll')`" --home `"$home_`" --seed `"$(Join-Path $InstallDirectory 'agent')`""

if ($PSCmdlet.ShouldProcess($name, "create the service: $command")) {
  New-Service -Name $name -DisplayName 'Juice Agent' -Description 'Juice Observability agent: read-only diagnostics and signed updates, outbound only.' `
    -BinaryPathName $command -StartupType Automatic | Out-Null
  # Its own virtual account, not LocalSystem.
  & sc.exe config $name obj= $account | Out-Null
  # Restart on failure, including a non-zero exit code.
  & sc.exe failure $name reset= 86400 actions= restart/5000/restart/30000/restart/60000 | Out-Null
  & sc.exe failureflag $name 1 | Out-Null

  # The service's environment: where its settings are, and its data directory.
  $key = "HKLM:\SYSTEM\CurrentControlSet\Services\$name"
  New-ItemProperty -Path $key -Name Environment -PropertyType MultiString -Force -Value @(
    "JUICE_AGENT_CONFIG=$ConfigFile",
    "Agent__DataDirectory=$DataDirectory",
    'DOTNET_NOLOGO=true'
  ) | Out-Null

  # Data and home: the service account and Administrators only.
  foreach ($directory in @($DataDirectory, $home_)) {
    New-Item -ItemType Directory -Force -Path $directory | Out-Null
    & icacls $directory /inheritance:r /grant:r "${account}:(OI)(CI)M" "Administrators:(OI)(CI)F" "SYSTEM:(OI)(CI)F" | Out-Null
  }
  # It reads its settings and keys, and never writes them.
  & icacls $ConfigFile /grant "${account}:R" | Out-Null

  Write-Host "Installed $name. Grant $account read access to the files agent.json names (the platform CA, release.pub, the enrollment token),"
  Write-Host "then: Start-Service $name. Warnings and errors of the launcher and the agent go to the Windows Application event log."
}
