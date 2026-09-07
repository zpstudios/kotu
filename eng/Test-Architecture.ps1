$ErrorActionPreference = 'Stop'
$repository = Split-Path $PSScriptRoot -Parent
$solution = Get-Content (Join-Path $repository 'KOTU.sln') -Raw
$graph = @{}
$projects = Get-ChildItem (Join-Path $repository 'src'), (Join-Path $repository 'tests') -Recurse -Filter '*.csproj'
foreach ($project in $projects) {
    $relative = [IO.Path]::GetRelativePath($repository, $project.FullName).Replace('/', '\')
    if (!$solution.Contains('"' + $relative + '"')) { throw "Project missing from solution: $relative" }
    [xml]$xml = Get-Content $project.FullName -Raw
    $references = @($xml.SelectNodes('//ProjectReference') | ForEach-Object {
        $target = [IO.Path]::GetFullPath((Join-Path $project.DirectoryName $_.Include))
        if (!(Test-Path -LiteralPath $target)) { throw "Broken project reference: $relative" }
        $target
    })
    $graph[$project.FullName] = $references
    if ($project.BaseName -in @('KOTU.DocumentModel', 'KOTU.FileOperations')) {
        if ($xml.SelectSingleNode('//TargetFramework').InnerText -ne 'net8.0') { throw "$relative must remain UI independent" }
        if ($references.Count -ne 0 -or $xml.SelectNodes('//PackageReference').Count -ne 0) {
            throw "$relative must not depend on UI, engine, or framework packages"
        }
    }
}

$visiting = @{}
$visited = @{}
function Test-DependencyNode([string]$node) {
    if ($visiting.ContainsKey($node)) { throw "Project dependency cycle: $node" }
    if ($visited.ContainsKey($node)) { return }
    if (!$graph.ContainsKey($node)) { throw "Dependency outside solution: $node" }
    $visiting[$node] = $true
    foreach ($dependency in $graph[$node]) { Test-DependencyNode $dependency }
    $visiting.Remove($node)
    $visited[$node] = $true
}
foreach ($node in @($graph.Keys)) { Test-DependencyNode $node }

$entries = [regex]::Matches($solution, 'Project\("[^"\r\n]+"\) = "[^"]+", "[^"\r\n]+\.csproj", "(?<id>\{[^}]+\})"')
foreach ($entry in $entries) {
    foreach ($configuration in @('Debug', 'Release')) {
        $mapping = $entry.Groups['id'].Value + '.' + $configuration + '|x64.Build.0'
        if (!$solution.Contains($mapping)) { throw "Missing solution build configuration: $mapping" }
    }
}
Write-Output "Architecture consistency passed: $($projects.Count) projects; references, dependency cycles, model isolation, solution build mappings."
