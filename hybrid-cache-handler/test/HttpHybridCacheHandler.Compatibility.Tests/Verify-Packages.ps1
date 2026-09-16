[CmdletBinding()]
param(
    [string] $ArtifactsPath = (Join-Path $PSScriptRoot '..\..\artifacts')
)

$ErrorActionPreference = 'Stop'
$packages = @{}
$contractFloors = [Collections.Generic.HashSet[string]]::new()
$frameworks = @{
    'net10.0' = @('net10.0', '.NETCoreApp10.0')
    'netstandard2.0' = @('netstandard2.0', '.NETStandard2.0')
    'net472' = @('net472', '.NETFramework4.7.2')
}
$names = @(
    'DamianH.HttpHybridCacheHandler',
    'DamianH.HttpHybridCacheHandler.ContentStore',
    'DamianH.HttpHybridCacheHandler.ContentStore.AzureBlob',
    'DamianH.HttpHybridCacheHandler.ContentStore.S3',
    'DamianH.HttpHybridCacheHandler.ContentStore.GoogleCloudStorage',
    'DamianH.HttpHybridCacheHandler.ContentStore.FileSystem'
)

foreach ($file in Get-ChildItem $ArtifactsPath -Filter '*.nupkg' -Recurse) {
    $archive = [IO.Compression.ZipFile]::OpenRead($file.FullName)
    try {
        $entry = @($archive.Entries | Where-Object FullName -Like '*.nuspec')
        if ($entry.Count -ne 1) { throw "Expected exactly one nuspec in $file" }
        $reader = [IO.StreamReader]::new($entry[0].Open())
        try { [xml] $nuspec = $reader.ReadToEnd() } finally { $reader.Dispose() }
        $id = [string] $nuspec.package.metadata.id
        if ($id -notin $names) { continue }
        if ($packages.ContainsKey($id)) { throw "Multiple versions of $id in artifacts; use a clean artifact directory." }
        $version = [string] $nuspec.package.metadata.version
        $assetGroups = @($archive.Entries | Where-Object FullName -Match '^lib/[^/]+/[^/]+\.dll$' |
            ForEach-Object { $_.FullName.Split('/')[1] } | Sort-Object -Unique)
        if ($assetGroups.Count -ne 3 -or @($assetGroups | Where-Object { -not $frameworks.ContainsKey($_) }).Count) {
            throw "$id has unexpected library framework groups: $assetGroups"
        }
        if (@($nuspec.package.metadata.dependencies.group).Count -ne 3) { throw "$id has unexpected dependency framework groups" }
        foreach ($tfm in $frameworks.Keys) {
            if (-not $archive.GetEntry("lib/$tfm/$id.dll")) { throw "$id is missing lib/$tfm/$id.dll" }
            $groups = @($nuspec.package.metadata.dependencies.group | Where-Object targetFramework -In $frameworks[$tfm])
            if ($groups.Count -ne 1) { throw "$id needs exactly one dependency group for $tfm" }
            foreach ($dependency in $groups[0].dependency) {
                if (-not $dependency.id -or -not $dependency.version) { throw "$id has an unversioned dependency for $tfm" }
                if ($dependency.id -match '^(MinVer|PolySharp|Microsoft.NETFramework.ReferenceAssemblies)') {
                    throw "$id leaked build-only dependency $($dependency.id)"
                }
            }
            if ($id -ne 'DamianH.HttpHybridCacheHandler.ContentStore' -and
                -not ($groups[0].dependency | Where-Object id -EQ 'DamianH.HttpHybridCacheHandler.ContentStore')) {
                throw "$id is missing the ContentStore dependency for $tfm"
            }
            foreach ($dependency in $groups[0].dependency | Where-Object id -EQ 'DamianH.HttpHybridCacheHandler.ContentStore') {
                [void] $contractFloors.Add(([string] $dependency.version).TrimStart('[').Split(',')[0].TrimEnd(']'))
            }
        }
        $packages[$id] = @{ Version = $version; Path = $file.FullName }
        Write-Host "Verified $id $version : net10.0, netstandard2.0, net472 assets/dependencies"
    }
    finally { $archive.Dispose() }
}
foreach ($id in $names) {
    if (-not $packages.ContainsKey($id)) { throw "Missing package: $id" }
}

# Use a private cache so a previously installed package with the same version cannot mask bad artifacts.
$work = Join-Path $PSScriptRoot ".package-smoke\$([Guid]::NewGuid().ToString('N'))"
$privatePackages = Join-Path $work 'packages'
$output = Join-Path $PSScriptRoot "bin\smoke-$([Guid]::NewGuid().ToString('N').Substring(0, 8))"
New-Item -ItemType Directory -Path $work -Force | Out-Null
try {
    $feed = Join-Path $work 'feed'
    New-Item -ItemType Directory -Path $feed | Out-Null
    foreach ($package in $packages.Values) { Copy-Item $package.Path $feed }
    $consumerVersions = @{}
    foreach ($id in $names) { $consumerVersions[$id] = $packages[$id].Version }
    $contractId = 'DamianH.HttpHybridCacheHandler.ContentStore'
    $floor = @($contractFloors | Sort-Object { [version] $_ } -Descending)[0]
    $builtVersion = $consumerVersions[$contractId]
    if ([version]($builtVersion.Split('-')[0]) -lt [version]$floor -or
        ([version]($builtVersion.Split('-')[0]) -eq [version]$floor -and $builtVersion.Contains('-'))) {
        # Dev MinVer prereleases precede the deliberate released dependency floor. Repack identical
        # contract binaries at that floor in this private feed, without changing release artifacts.
        Write-Host "Smoke-only ContentStore package version $floor substitutes for $builtVersion to satisfy the released dependency floor."
        $contractProject = Join-Path $PSScriptRoot '..\..\src\HttpHybridCacheHandler.ContentStore'
        & dotnet pack $contractProject --no-build -c Release -o $feed "-p:PackageVersion=$floor" "-p:MinVerVersionOverride=$floor" -v minimal
        if ($LASTEXITCODE) { throw 'Cannot pack the smoke-only ContentStore dependency floor.' }
        if (-not (Test-Path (Join-Path $feed "$contractId.$floor.nupkg"))) { throw 'Smoke-only contract package has the wrong version.' }
        $consumerVersions[$contractId] = $floor
    }
    $escapedFeed = [Security.SecurityElement]::Escape($feed)
    @"
<configuration>
  <packageSources><clear /><add key="local" value="$escapedFeed" /><add key="nuget.org" value="https://api.nuget.org/v3/index.json" /></packageSources>
  <packageSourceMapping>
    <clear />
    <packageSource key="local"><package pattern="DamianH.HttpHybridCacheHandler*" /></packageSource>
    <packageSource key="nuget.org"><package pattern="*" /></packageSource>
  </packageSourceMapping>
</configuration>
"@ | Set-Content (Join-Path $work 'NuGet.Config')
    foreach ($tfm in @('net10.0', 'netstandard2.0', 'net472')) {
        $projectDirectory = Join-Path $work $tfm
        New-Item -ItemType Directory -Path $projectDirectory | Out-Null
        $outputType = if ($tfm -eq 'netstandard2.0') { 'Library' } else { 'Exe' }
        $references = ($names | ForEach-Object {
            "    <PackageReference Include=`"$_`" Version=`"[$($consumerVersions[$_])]`" />"
        }) -join [Environment]::NewLine
        $extra = if ($tfm -eq 'net472') {
            '<Reference Include="System.Net.Http" /><PackageReference Include="Microsoft.NETFramework.ReferenceAssemblies" Version="1.0.3" PrivateAssets="all" />'
        } else { '' }
        $escapedOutput = [Security.SecurityElement]::Escape($output + [IO.Path]::DirectorySeparatorChar)
        @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>$tfm</TargetFramework><OutputType>$outputType</OutputType>
    <ImportDirectoryBuildProps>false</ImportDirectoryBuildProps><ImportDirectoryBuildTargets>false</ImportDirectoryBuildTargets>
    <LangVersion>latest</LangVersion><Nullable>enable</Nullable><TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <BaseOutputPath>$escapedOutput</BaseOutputPath>
    <AutoGenerateBindingRedirects>true</AutoGenerateBindingRedirects><GenerateBindingRedirectsOutputType>true</GenerateBindingRedirectsOutputType>
  </PropertyGroup>
  <ItemGroup>
$references
    <PackageReference Include="Microsoft.Extensions.DependencyInjection" Version="10.0.10" />
    <PackageReference Include="Microsoft.Extensions.Http" Version="10.0.10" />
    $extra
  </ItemGroup>
</Project>
"@ | Set-Content (Join-Path $projectDirectory 'Consumer.csproj')
        Copy-Item (Join-Path $PSScriptRoot 'PackageConsumer.cs.txt') (Join-Path $projectDirectory 'Program.cs')
        $project = Join-Path $projectDirectory 'Consumer.csproj'
        & dotnet restore $project --configfile (Join-Path $work 'NuGet.Config') --packages $privatePackages -v minimal
        if ($LASTEXITCODE) { throw "Package consumer restore failed: $tfm" }
        $assets = Get-Content (Join-Path $projectDirectory 'obj\project.assets.json') -Raw | ConvertFrom-Json -AsHashtable
        $actualPackagesPath = [IO.Path]::GetFullPath($assets.project.restore.packagesPath).TrimEnd([char[]]'\/')
        $expectedPackagesPath = [IO.Path]::GetFullPath($privatePackages).TrimEnd([char[]]'\/')
        $comparison = if ($IsWindows) { [StringComparison]::OrdinalIgnoreCase } else { [StringComparison]::Ordinal }
        if (-not [string]::Equals($actualPackagesPath, $expectedPackagesPath, $comparison)) {
            throw "$tfm consumer did not use its isolated NuGet package cache: $actualPackagesPath"
        }
        $target = @($assets.targets.Values)[0]
        foreach ($id in $names) {
            $key = "$id/$($consumerVersions[$id])"
            $compile = $target[$key].compile.Keys
            if ("lib/$tfm/$id.dll" -notin $compile) { throw "$id $tfm consumer selected unexpected compile assets: $compile" }
            $runtime = $target[$key].runtime.Keys
            if ("lib/$tfm/$id.dll" -notin $runtime) { throw "$id $tfm consumer selected unexpected runtime assets: $runtime" }
        }
        & dotnet build $project -c Release --no-restore --disable-build-servers -m:1 -v minimal
        if ($LASTEXITCODE) { throw "Package consumer build failed: $tfm (exit $LASTEXITCODE)" }
        if ($tfm -eq 'net10.0') {
            & dotnet (Join-Path $output 'Release\net10.0\Consumer.dll')
            if ($LASTEXITCODE) { throw "Package consumer execution failed: $tfm" }
        }
        elseif ($tfm -eq 'net472' -and $IsWindows) {
            & (Join-Path $output 'Release\net472\Consumer.exe')
            if ($LASTEXITCODE) { throw "Package consumer execution failed: $tfm" }
        }
        else {
            Write-Host "Verified $tfm consumer compile/runtime asset selection and build (not executed on this host)."
        }
    }
}
finally {
    Remove-Item $work -Recurse -Force
    if (Test-Path $output) { Remove-Item $output -Recurse -Force }
}
