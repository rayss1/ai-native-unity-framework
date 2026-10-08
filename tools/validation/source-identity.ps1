# Worktree evidence is an explicit development check, never exact-commit release qualification.
function Get-AiNativeSourceManifest {
    param([Parameter(Mandatory)][string]$Root)
    $Root = [IO.Path]::GetFullPath($Root)
    $paths = @(& git -C $Root -c core.quotepath=false ls-files --cached --others --exclude-standard)
    if ($LASTEXITCODE -ne 0) { throw 'Cannot enumerate source manifest' }
    $paths = @($paths | Sort-Object -Unique -CaseSensitive)
    $files = @(foreach ($path in $paths) {
        if ($path -eq 'server/vendor/Fantasy') { continue } # Gitlink is verified separately.
        $absolute = [IO.Path]::GetFullPath((Join-Path $Root $path))
        if (-not $absolute.StartsWith($Root + [IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) { throw 'Source manifest path escapes repository' }
        if (-not (Test-Path -LiteralPath $absolute -PathType Leaf)) { throw "Source manifest file is missing: $path" }
        [pscustomobject]@{ path=$path.Replace('\','/'); sha256=(Get-FileHash -LiteralPath $absolute -Algorithm SHA256).Hash.ToLowerInvariant() }
    })
    if ($files.Count -eq 0) { throw 'Source manifest is empty' }
    $canonical = ($files | ForEach-Object { $_.path + "`t" + $_.sha256 }) -join "`n"
    $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($canonical))).ToLowerInvariant()
    return [pscustomobject]@{ algorithm='sha256-path-tab-filehash-lf-v1'; sha256=$hash; files=$files }
}

function Assert-AiNativeSourceManifest {
    param([Parameter(Mandatory)][string]$Root,[Parameter(Mandatory)][string]$ExpectedSha256)
    $manifest = Get-AiNativeSourceManifest -Root $Root
    if ($manifest.sha256 -ne $ExpectedSha256) { throw 'Worktree source manifest mismatch; source changed or wrong snapshot supplied' }
    return $manifest
}
