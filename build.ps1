# build.ps1 - 编译 MousePointerReposition
# 用法:
#   .\build.ps1                 # Debug + Release
#   .\build.ps1 -Configuration Debug
#   .\build.ps1 -Configuration Release
#   .\build.ps1 -Configuration All -Platform x64 -Package   # 额外构建 MSIX 打包工程
param(
    [ValidateSet('Debug', 'Release', 'All')]
    [string]$Configuration = 'All',

    [ValidateSet('Any CPU', 'x86', 'x64')]
    [string]$Platform = 'Any CPU',

    # 是否同时构建 MSIX 打包工程 (PackageProject.wapproj, 需要 VS 桌面桥接组件)
    [switch]$Package
)

$ErrorActionPreference = 'Stop'

# 定位 MSBuild
$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
$msbuild = & $vswhere -latest -requires Microsoft.Component.MSBuild -find "MSBuild\**\Bin\MSBuild.exe" | Select-Object -First 1
if (-not $msbuild) { $msbuild = "${env:ProgramFiles}\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe" }
if (-not (Test-Path $msbuild)) { throw "未找到 MSBuild，请先安装 Visual Studio 2022" }

$root    = Split-Path -Parent $MyInvocation.MyCommand.Path
$sln     = Join-Path $root "MousePointerReposition.sln"
$appProj = Join-Path $root "MousePointerReposition\MousePointerReposition.csproj"
$wapProj = Join-Path $root "PackageProject\PackageProject.wapproj"

$configs = if ($Configuration -eq 'All') { @('Debug', 'Release') } else { @($Configuration) }

foreach ($cfg in $configs) {
    Write-Host "`n===== 编译 $cfg | $Platform =====" -ForegroundColor Cyan

    # 主程序 (WPF, .NET Framework 4.7.2)
    # csproj 中平台写作 AnyCPU(无空格), 这里去掉空格
    $appPlatform = $Platform -replace ' ', ''
    & $msbuild $appProj /t:Restore,Build `
        /p:Configuration=$cfg /p:Platform="$appPlatform" `
        /v:minimal /nologo
    if ($LASTEXITCODE -ne 0) { throw "编译失败: $cfg" }

    # MSIX 打包工程
    if ($Package) {
        Write-Host "----- 打包 MSIX ($cfg) -----" -ForegroundColor Cyan
        & $msbuild $wapProj /t:Build `
            /p:Configuration=$cfg /p:Platform="$Platform" `
            /p:AppxPackageSigningEnabled=false `
            /v:minimal /nologo
        if ($LASTEXITCODE -ne 0) { throw "打包失败: $cfg" }
    }
}

Write-Host "`n全部完成。输出目录: MousePointerReposition\bin\$($Platform -replace ' ','')\{Debug|Release}\" -ForegroundColor Green
