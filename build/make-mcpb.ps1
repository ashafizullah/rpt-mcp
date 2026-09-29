<#
  Builds rpt-mcp-<version>.mcpb (an MCP Bundle: a zip with manifest.json at the root) from a Release build.
  The tool list in the manifest is read from the server itself (tools/list), so it never goes stale.
  Usage: pwsh build/make-mcpb.ps1 -Version 0.1.2 [-OutDir dist]
#>
param(
  [Parameter(Mandatory)] [string] $Version,
  [string] $OutDir = "dist"
)
$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$bin = Join-Path $root "src/bin/Release"
$exe = Join-Path $bin "rpt-mcp.exe"
if (-not (Test-Path $exe)) { throw "Build first: $exe not found" }

# Ask the server for its tools.
$psi = New-Object System.Diagnostics.ProcessStartInfo $exe
$psi.UseShellExecute = $false; $psi.RedirectStandardInput = $true; $psi.RedirectStandardOutput = $true; $psi.RedirectStandardError = $true
$p = [System.Diagnostics.Process]::Start($psi)
$p.StandardInput.WriteLine('{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}')
$p.StandardInput.WriteLine('{"jsonrpc":"2.0","id":2,"method":"tools/list"}')
$p.StandardInput.Close()
$lines = @(); while ($null -ne ($l = $p.StandardOutput.ReadLine())) { $lines += $l }
$p.WaitForExit()
$tools = ($lines | ForEach-Object { $_ | ConvertFrom-Json } | Where-Object { $_.id -eq 2 }).result.tools |
  ForEach-Object {
    # First sentence only; "e.g." / "i.e." do not end a sentence.
    $m = [regex]::Match($_.description, '^.*?(?<!\be\.g|\bi\.e)\.(?=\s|$)')
    [ordered]@{ name = $_.name; description = $(if ($m.Success) { $m.Value } else { $_.description }) }
  }
if (-not $tools) { throw "Could not read tools/list from rpt-mcp.exe" }

$manifest = [ordered]@{
  manifest_version = "0.3"
  name             = "rpt-mcp"
  display_name     = "rpt-mcp (SAP Crystal Reports)"
  version          = $Version
  description      = "Inspect and edit SAP Crystal Reports .rpt files: formulas, parameters, data sources, text, fonts, field formats, lines, pictures."
  long_description = "MCP server for SAP Crystal Reports .rpt files. Requires Windows and the SAP Crystal Reports runtime for .NET Framework (x64), which is not included. Not affiliated with SAP SE."
  author           = [ordered]@{ name = "Adam Suchi Hafizullah"; url = "https://github.com/ashafizullah" }
  repository       = [ordered]@{ type = "git"; url = "https://github.com/ashafizullah/rpt-mcp.git" }
  homepage         = "https://github.com/ashafizullah/rpt-mcp"
  documentation    = "https://github.com/ashafizullah/rpt-mcp#readme"
  support          = "https://github.com/ashafizullah/rpt-mcp/issues"
  license          = "MIT"
  keywords         = @("crystal-reports", "rpt", "reporting", "sap", "windows")
  server           = [ordered]@{
    type        = "binary"
    entry_point = "server/rpt-mcp.exe"
    mcp_config  = [ordered]@{ command = '${__dirname}/server/rpt-mcp.exe'; args = @(); env = [ordered]@{} }
  }
  compatibility    = [ordered]@{ platforms = @("win32") }
  tools            = @($tools)
}

$stage = Join-Path ([System.IO.Path]::GetTempPath()) "rpt-mcp-mcpb-$Version"
Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force (Join-Path $stage "server") | Out-Null
Copy-Item (Join-Path $bin "rpt-mcp.exe"), (Join-Path $bin "rpt-mcp.exe.config"), (Join-Path $bin "Newtonsoft.Json.dll") (Join-Path $stage "server")
Copy-Item (Join-Path $root "README.md"), (Join-Path $root "LICENSE"), (Join-Path $root "THIRD-PARTY-NOTICES.md") $stage
if (Get-ChildItem $stage -Recurse -Filter "CrystalDecisions*") { throw "SAP assemblies must not be packaged" }
$manifest | ConvertTo-Json -Depth 10 | Set-Content -Encoding utf8NoBOM (Join-Path $stage "manifest.json")

New-Item -ItemType Directory -Force $OutDir | Out-Null
$out = Join-Path (Resolve-Path $OutDir) "rpt-mcp-$Version.mcpb"
Remove-Item $out -ErrorAction SilentlyContinue
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory($stage, $out)
Write-Output "Created $out ($($tools.Count) tools)"
