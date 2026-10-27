#requires -Version 7.0
[CmdletBinding()]
param(
    [string]$ModulePath,
    [string[]]$Path,
    [switch]$Check
)

$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
if (-not $ModulePath) {
    $ModulePath = Join-Path $repo 'artifacts/tool-formatting/modules/PSScriptAnalyzer/1.24.0/PSScriptAnalyzer.psd1'
}
Import-Module $ModulePath -RequiredVersion 1.24.0
$settings = Join-Path $PSScriptRoot 'powershell.psd1'

function Read-ScriptTokens([string]$Text) {
    $tokens = $null
    $errors = $null
    $ast = [Management.Automation.Language.Parser]::ParseInput($Text, [ref]$tokens, [ref]$errors)
    if ($errors.Count) {
        throw ($errors | Out-String)
    }
    return @{
        Ast = $ast
        Tokens = $tokens
    }
}

function Get-TokenIdentity($Tokens) {
    return @($Tokens | Where-Object Kind -NotIn @('NewLine', 'Semi', 'EndOfInput') | ForEach-Object {
            [pscustomobject]@{
                Kind = [string]$_.Kind
                Text = $_.Text
            }
        }) | ConvertTo-Json -Depth 3 -Compress
}

function Expand-ScriptBlocks([string]$Text, $Parsed) {
    $newline = if ($Text.Contains("`r`n")) {
        "`r`n"
    } else {
        "`n"
    }
    $forHeaders = @($Parsed.Ast.FindAll({
                param($node)
                $node -is [Management.Automation.Language.ForStatementAst]
            }, $true) | ForEach-Object {
            @{
                Start = $_.Extent.StartOffset
                End = $_.Body.Extent.StartOffset
            }
        })
    $edits = @()
    foreach ($token in $Parsed.Tokens) {
        $start = $token.Extent.StartOffset
        $end = $token.Extent.EndOffset
        if ($token.Kind -in @('LCurly', 'AtCurly')) {
            if ($end -lt $Text.Length -and $Text[$end] -notin @("`r", "`n")) {
                $edits += @{
                    Start = $end
                    Length = 0
                    Text = $newline
                }
            }
        } elseif ($token.Kind -eq 'RCurly') {
            $lineStart = $Text.LastIndexOf("`n", [Math]::Max(0, $start - 1)) + 1
            if ($Text.Substring($lineStart, $start - $lineStart).Trim().Length) {
                $edits += @{
                    Start = $start
                    Length = 0
                    Text = $newline
                }
            }
        } elseif ($token.Kind -eq 'Semi') {
            $insideFor = @($forHeaders | Where-Object {
                    $start -ge $_.Start -and $start -lt $_.End
                }).Count -gt 0
            if (-not $insideFor) {
                $edits += @{
                    Start = $start
                    Length = $end - $start
                    Text = $newline
                }
            }
        }
    }
    foreach ($edit in ($edits | Sort-Object Start -Descending)) {
        $Text = $Text.Remove($edit.Start, $edit.Length).Insert($edit.Start, $edit.Text)
    }
    return $Text
}

function Remove-TrailingTrivia([string]$Text) {
    $parsed = Read-ScriptTokens $Text
    $matches = [regex]::Matches($Text, '[\t ]+(?=\r?$)', [Text.RegularExpressions.RegexOptions]::Multiline)
    for ($index = $matches.Count - 1; $index -ge 0; $index--) {
        $match = $matches[$index]
        $insideToken = @($parsed.Tokens | Where-Object {
                $_.Kind -notin @('NewLine', 'EndOfInput') -and
                $_.Extent.StartOffset -le $match.Index -and $_.Extent.EndOffset -gt $match.Index
            }).Count -gt 0
        if (-not $insideToken) {
            $Text = $Text.Remove($match.Index, $match.Length)
        }
    }
    return $Text
}

function Collapse-EmptyBlocks([string]$Text) {
    $parsed = Read-ScriptTokens $Text
    $previous = $null
    $edits = @()
    foreach ($token in $parsed.Tokens) {
        if ($token.Kind -eq 'NewLine') {
            continue
        }
        if ($token.Kind -eq 'RCurly' -and $previous -and $previous.Kind -in @('LCurly', 'AtCurly')) {
            $start = $previous.Extent.EndOffset
            $length = $token.Extent.StartOffset - $start
            if ($length -gt 0 -and [string]::IsNullOrWhiteSpace($Text.Substring($start, $length))) {
                $edits += @{
                    Start = $start
                    Length = $length
                }
            }
        }
        $previous = $token
    }
    foreach ($edit in ($edits | Sort-Object Start -Descending)) {
        $Text = $Text.Remove($edit.Start, $edit.Length)
    }
    return $Text
}

if (-not $Path) {
    $Path = @(Get-ChildItem (Join-Path $repo 'tools') -Recurse -File -Filter '*.ps1' |
            Sort-Object FullName | Select-Object -ExpandProperty FullName)
}
$changed = 0
foreach ($file in $Path) {
    $original = [IO.File]::ReadAllText($file)
    $parsed = Read-ScriptTokens $original
    $expanded = Expand-ScriptBlocks $original $parsed
    $formatted = Invoke-Formatter -ScriptDefinition $expanded -Settings $settings
    $formatted = Collapse-EmptyBlocks $formatted
    $formatted = Remove-TrailingTrivia $formatted
    $verified = Read-ScriptTokens $formatted
    if ((Get-TokenIdentity $parsed.Tokens) -cne (Get-TokenIdentity $verified.Tokens)) {
        throw "Formatting changed non-layout tokens: $file"
    }
    if ($original -cne $formatted) {
        $changed++
        Write-Host "$(if ($Check) { 'Would format' } else { 'Formatted' }): $file"
        if (-not $Check) {
            [IO.File]::WriteAllText($file, $formatted, [Text.UTF8Encoding]::new($false))
        }
    }
}
Write-Host "PowerShell formatting: $($Path.Count) files checked, $changed changed; non-layout tokens preserved."
if ($Check -and $changed) {
    exit 1
}
