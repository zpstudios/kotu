function Test-StandaloneProductName([string]$Name) { $Name -match '^(KOTU|ZP|WinUtil)(\.|$)' }

function Get-AppDataSnapshot([string[]]$BaseFolders = @($env:APPDATA, $env:LOCALAPPDATA)) {
    foreach ($base in $BaseFolders) {
        foreach ($brand in @('KOTU', 'ZP', 'WinUtil')) {
            $folder = Join-Path $base $brand
            # Include root existence: creating an empty product directory is also a trace.
            "root:${folder}:$(Test-Path -LiteralPath $folder)"
            if (Test-Path -LiteralPath $folder) {
                Get-ChildItem -LiteralPath $folder -Recurse -Force | ForEach-Object {
                    if ($_.PSIsContainer) { "directory:$($_.FullName)" }
                    else { "file:$($_.FullName):$((Get-FileHash -LiteralPath $_.FullName).Hash)" }
                }
            }
        }
    }
}

function Get-StandaloneRegistryKeySnapshot([string]$Path, [switch]$Recurse,
    [scriptblock]$ValueFilter = { param($name, $value) $true }, [switch]$ValuesOnly) {
    $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($Path)
    if (!$key) { return }
    try {
        if (!$ValuesOnly) { "key:$($key.Name)" }
        foreach ($name in $key.GetValueNames() | Sort-Object) {
            $value = $key.GetValue($name, $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
            if (& $ValueFilter $name $value) {
                $encoded = ConvertTo-Json -InputObject $value -Depth 5 -Compress
                "value:$($key.Name):${name}:$($key.GetValueKind($name)):$encoded"
            }
        }
        if ($Recurse) {
            foreach ($child in $key.GetSubKeyNames() | Sort-Object) {
                Get-StandaloneRegistryKeySnapshot "$Path\$child" -Recurse
            }
        }
    }
    finally { $key.Dispose() }
}

function Get-StandaloneRegistrySubKeyNames([string]$Path) {
    $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($Path)
    if (!$key) { return }
    try { $key.GetSubKeyNames() } finally { $key.Dispose() }
}

function Get-RegistrySnapshot {
    foreach ($brand in @('KOTU', 'ZP', 'WinUtil')) {
        Get-StandaloneRegistryKeySnapshot "Software\$brand" -Recurse
        Get-StandaloneRegistryKeySnapshot "Software\Classes\Applications\$brand.exe" -Recurse
    }
    Get-StandaloneRegistryKeySnapshot 'Software\RegisteredApplications' -ValuesOnly -ValueFilter {
        param($name, $value) $name -in @('KOTU', 'ZP', 'WinUtil')
    }
    $classes = @(Get-StandaloneRegistrySubKeyNames 'Software\Classes')
    foreach ($name in $classes | Where-Object { Test-StandaloneProductName $_ }) {
        Get-StandaloneRegistryKeySnapshot "Software\Classes\$name" -Recurse
    }
    foreach ($extension in $classes | Where-Object { $_.StartsWith('.') }) {
        Get-StandaloneRegistryKeySnapshot "Software\Classes\$extension\OpenWithProgids" -ValuesOnly -ValueFilter {
            param($name, $value) Test-StandaloneProductName $name
        }
    }
    $shellParents = @('Software\Classes\*\shell', 'Software\Classes\Directory\shell')
    foreach ($extension in Get-StandaloneRegistrySubKeyNames 'Software\Classes\SystemFileAssociations') {
        $shellParents += "Software\Classes\SystemFileAssociations\$extension\shell"
    }
    foreach ($parent in $shellParents) {
        foreach ($verb in Get-StandaloneRegistrySubKeyNames $parent | Where-Object { Test-StandaloneProductName $_ }) {
            Get-StandaloneRegistryKeySnapshot "$parent\$verb" -Recurse
        }
    }
    # These are the three product-owned COM classes in ShellVerbServer; other CLSIDs are OS-owned.
    foreach ($clsid in @('{041C5095-1D24-47DD-9B0C-5151269D9F6A}',
                        '{DD20BBAF-4FF9-49E9-8BB4-D4C361B5AC77}', '{DF11F069-F9E7-4D06-86D1-AEA7555D595E}')) {
        Get-StandaloneRegistryKeySnapshot "Software\Classes\CLSID\$clsid" -Recurse
    }
    $fileExts = 'Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts'
    foreach ($extension in Get-StandaloneRegistrySubKeyNames $fileExts) {
        Get-StandaloneRegistryKeySnapshot "$fileExts\$extension\OpenWithProgids" -ValuesOnly -ValueFilter {
            param($name, $value) Test-StandaloneProductName $name
        }
        $choice = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey("$fileExts\$extension\UserChoice")
        if ($choice) {
            try {
                if (Test-StandaloneProductName ([string]$choice.GetValue('ProgId'))) {
                    Get-StandaloneRegistryKeySnapshot "$fileExts\$extension\UserChoice" -Recurse
                }
            }
            finally { $choice.Dispose() }
        }
    }
    # NotifyIconSettings, MRU and execution history are maintained by Windows. Their volatile
    # additions are deliberately excluded; standalone code disables the app's tray-promotion writer.
}
