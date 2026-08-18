param(
    [switch]$AllowBuildArtifacts,
    [string[]]$ForbiddenStrings = @()
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$errors = New-Object System.Collections.Generic.List[string]

function Add-Problem([string]$message) {
    $script:errors.Add($message)
}

# Values the operator never wants to see in the tree or in a shipped binary.
# They arrive at run time, through -ForbiddenStrings or the comma separated
# LIBERAR_VERIFY_FORBIDDEN variable, so this repository never records one.
function Get-ForbiddenStrings([string[]]$fromParameter) {
    $values = @($fromParameter)
    if (-not [string]::IsNullOrWhiteSpace($env:LIBERAR_VERIFY_FORBIDDEN)) {
        $values += $env:LIBERAR_VERIFY_FORBIDDEN -split '[,;]'
    }
    return $values |
        ForEach-Object { $_.Trim() } |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
}

function Is-AllowedIPv4([System.Net.IPAddress]$address) {
    $bytes = $address.GetAddressBytes()
    if ($bytes.Length -ne 4) { return $true }

    $a, $b, $c, $d = $bytes
    if ($a -eq 0 -or $a -eq 10 -or $a -eq 127 -or $a -ge 224) { return $true }
    if ($a -eq 100 -and $b -ge 64 -and $b -le 127) { return $true }
    if ($a -eq 169 -and $b -eq 254) { return $true }
    if ($a -eq 172 -and $b -ge 16 -and $b -le 31) { return $true }
    if ($a -eq 192 -and $b -eq 168) { return $true }
    if ($a -eq 192 -and $b -eq 0) { return $true }
    if ($a -eq 192 -and $b -eq 0 -and $c -eq 2) { return $true }
    if ($a -eq 198 -and ($b -eq 18 -or $b -eq 19)) { return $true }
    if ($a -eq 198 -and $b -eq 51 -and $c -eq 100) { return $true }
    if ($a -eq 203 -and $b -eq 0 -and $c -eq 113) { return $true }
    if ($a -eq 255) { return $true }
    return $false
}

$gitMetadataRoot = Join-Path $root '.git'
$allFiles = Get-ChildItem -LiteralPath $root -Recurse -Force -File | Where-Object {
    -not $_.FullName.StartsWith(
        $gitMetadataRoot + [IO.Path]::DirectorySeparatorChar,
        [StringComparison]::OrdinalIgnoreCase)
}
foreach ($file in $allFiles) {
    $relative = $file.FullName.Substring($root.Length).TrimStart('\', '/') -replace '\\', '/'

    if ($relative -match '(^|/)\.git(/|$)' -or $relative -eq '.gitmodules') {
        Add-Problem "Git metadata is forbidden: $relative"
    }
    if ($relative -match '(^|/)\.ssh(/|$)' -or
        $relative -match '(^|/)(broker\.url|painel\.url|proxy_config\.local\.hpp)$' -or
        $relative -match '(^|/)(id_rsa|id_ed25519|authorized_keys)$') {
        Add-Problem "Private configuration path is forbidden: $relative"
    }
    if ($relative -match '(^|/)(AUDIT-|CAPACITY\.md|DISTRIBUTION\.md|VPS-ACCESS\.md)' -or
        $relative -match '(^|/)(exa-results|audit-output)(/|$)') {
        Add-Problem "Research or operational document is forbidden: $relative"
    }

    $isBuildOutput = $relative -match '(^|/)(bin|obj|dist|x64)(/|$)' -or
        $relative -match '\.(exe|dll|pdb|obj|lib|zip|har)$' -or
        $relative -eq 'server/droute-broker'
    if ($isBuildOutput -and -not $AllowBuildArtifacts) {
        Add-Problem "Build artifact is forbidden in the source package: $relative"
    }
    if ($relative -match '\.(pem|key|pfx|p12|env)$') {
        Add-Problem "Credential-like file is forbidden: $relative"
    }
}

$textExtensions = @(
    '.cs', '.cpp', '.c', '.h', '.hpp', '.go', '.mod', '.sum', '.json',
    '.xml', '.resx', '.config', '.csproj', '.vcxproj', '.filters', '.sln',
    '.md', '.txt', '.yml', '.yaml', '.sh', '.ps1', '.targets', '.gitignore'
)

$operatorStrings = @(Get-ForbiddenStrings $ForbiddenStrings)

foreach ($file in $allFiles) {
    $relative = $file.FullName.Substring($root.Length).TrimStart('\', '/') -replace '\\', '/'
    if ($relative -eq 'scripts/verify-public.ps1') { continue }
    if ($AllowBuildArtifacts -and
        $relative -match '(^|/)(bin|obj|dist|x64|lib)(/|$)') { continue }
    if ($textExtensions -notcontains $file.Extension.ToLowerInvariant() -and
        $file.Name -ne '.gitignore') { continue }

    $text = [IO.File]::ReadAllText($file.FullName)
    $checks = @(
        @{ Pattern = '(?i)C:\\Users\\'; Label = 'Windows user path' },
        @{ Pattern = '(?i)/(Users|home)/[^/\s]+/'; Label = 'home directory path' },
        @{ Pattern = '(?i)/root/'; Label = 'root home path' },
        @{ Pattern = '(?i)\b[a-z0-9-]+\.liberar\.live\b'; Label = 'operational liberar.live subdomain' },
        @{ Pattern = '(?i)\bvps[0-9]{4,}\b'; Label = 'provider hostname' },
        @{ Pattern = '-----BEGIN [A-Z ]*PRIVATE KEY-----'; Label = 'private key material' },
        @{ Pattern = '(?i)\bssh-(rsa|ed25519)\s+[A-Za-z0-9+/]+'; Label = 'SSH key material' }
    )
    foreach ($check in $checks) {
        if ([regex]::IsMatch($text, $check.Pattern)) {
            Add-Problem "$($check.Label) found in $relative"
        }
    }
    foreach ($secret in $operatorStrings) {
        if ($text.IndexOf($secret, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
            Add-Problem "Operator-supplied forbidden string found in $relative"
        }
    }

    if ($relative -notmatch '^external/minhook/' -and
        $relative -notmatch '^third_party/' -and
        [regex]::IsMatch($text, '[A-Za-z0-9._%+-]+@[A-Za-z0-9](?:[A-Za-z0-9.-]*[A-Za-z0-9])?\.[A-Za-z]{2,}')) {
        Add-Problem "First-party email address found in $relative"
    }

    $skipIPv4ForGeneratedVersion = $file.Name -in @(
        'AssemblyInfo.cs', 'Resources.Designer.cs', 'Settings.Designer.cs'
    )
    if ($skipIPv4ForGeneratedVersion) { continue }

    foreach ($match in [regex]::Matches($text, '(?<![0-9])(?:[0-9]{1,3}\.){3}[0-9]{1,3}(?![0-9])')) {
        $lineStart = $text.LastIndexOf("`n", [Math]::Max(0, $match.Index - 1)) + 1
        $lineEnd = $text.IndexOf("`n", $match.Index)
        if ($lineEnd -lt 0) { $lineEnd = $text.Length }
        $line = $text.Substring($lineStart, $lineEnd - $lineStart)
        if ($line -match '(Assembly(File)?Version|FileVersion|ProductVersion|\bVersion\s*=)') {
            continue
        }
        $parsed = $null
        if ([System.Net.IPAddress]::TryParse($match.Value, [ref]$parsed) -and
            -not (Is-AllowedIPv4 $parsed)) {
            Add-Problem "Public IPv4 address $($match.Value) found in $relative"
        }
    }
}

if ($AllowBuildArtifacts) {
    # Literals that are wrong in a shipped binary no matter what. Machine
    # specific ones come from the environment and operational ones are matched
    # by shape further down, so no real address is written here.
    $binaryPatterns = @(
        $root,
        [Environment]::GetFolderPath([Environment+SpecialFolder]::UserProfile),
        $env:USERPROFILE,
        $env:HOME,
        '.ssh',
        '-----BEGIN PRIVATE KEY-----'
    ) + $operatorStrings |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
        Select-Object -Unique
    $binaryChecks = @(
        @{ Pattern = '(?i)\b[a-z0-9-]+\.liberar\.live\b'; Label = 'operational liberar.live subdomain' },
        @{ Pattern = '(?i)\bvps[0-9]{4,}\b'; Label = 'provider hostname' },
        @{ Pattern = '(?i)C:\\Users\\'; Label = 'Windows user path' }
    )
    $distributedBinaries = @(
        'installer/bin/Release/liberar.live.exe',
        'dist/x64/Release/droute_64.dll',
        'updaterHook/bin/Release/Droute.UpdaterHook.dll',
        'server/droute-broker'
    )
    $binaries = $allFiles | Where-Object {
        $relative = $_.FullName.Substring($root.Length).TrimStart('\', '/') -replace '\\', '/'
        $distributedBinaries -contains $relative
    }
    foreach ($file in $binaries) {
        $relative = $file.FullName.Substring($root.Length).TrimStart('\', '/')
        $bytes = [IO.File]::ReadAllBytes($file.FullName)
        $ascii = [Text.Encoding]::ASCII.GetString($bytes)
        $unicode = [Text.Encoding]::Unicode.GetString($bytes)
        foreach ($pattern in $binaryPatterns) {
            if ($ascii.Contains($pattern) -or $unicode.Contains($pattern)) {
                Add-Problem "Sensitive build string found in $relative"
            }
        }
        foreach ($check in $binaryChecks) {
            if ([regex]::IsMatch($ascii, $check.Pattern) -or
                [regex]::IsMatch($unicode, $check.Pattern)) {
                Add-Problem "$($check.Label) found in $relative"
            }
        }
    }
}

if ($errors.Count -gt 0) {
    $errors | Sort-Object -Unique | ForEach-Object { Write-Error $_ }
    exit 1
}

Write-Host "Public-source verification passed ($($allFiles.Count) files)."
