#Requires -Version 5.1
<#
.SYNOPSIS
    NotchPeninsula 打包脚本：清理构建产物，发布为不打包运行时的单文件 exe。

.DESCRIPTION
    不打包 .NET 运行时（目标机需自备 .NET 10 Desktop Runtime，否则会提示 "You must install .NET"），
    只把程序自身、依赖 dll（NAudio / SkiaSharp / WinRT 等）和资源文件
    （data 图片、vcruntime140.dll、msvcp140.dll 等）全部合并进单个 exe，
    直接输出到 publish 目录，产物约 35 MB。

.PARAMETER Clean
    仅清理构建产物，不执行打包。

.PARAMETER Output
    发布输出目录（相对仓库根目录），默认 publish。

.EXAMPLE
    .\build.ps1
    清理后打包，产出 publish\NotchPeninsula.exe（约 35 MB）。

.EXAMPLE
    .\build.ps1 -Clean
    只清理 bin/obj/publish，不打包。
#>
[CmdletBinding()]
param(
    [switch]$Clean,

    [string]$Output = 'publish'
)

$ErrorActionPreference = 'Stop'

$root = $PSScriptRoot
$project = Join-Path $root 'NotchPeninsula.csproj'
$outDir = Join-Path $root $Output

function Remove-Path($path) {
    if (Test-Path $path) {
        Remove-Item -Recurse -Force $path
        Write-Host "  已删除 $path" -ForegroundColor DarkGray
    }
}

function Remove-BuildArtifacts {
    # 清理编译产物与上一次的发布结果
    Remove-Path (Join-Path $root 'bin')
    Remove-Path (Join-Path $root 'obj')
    Remove-Path $outDir
}

# 本程序常驻托盘，从 bin 下运行时会把产物锁住，导致清理失败
$running = @(Get-Process -Name 'NotchPeninsula' -ErrorAction SilentlyContinue)
if ($running.Count -gt 0) {
    throw "检测到 NotchPeninsula 正在运行（PID: $($running.Id -join ', ')），请先从托盘菜单退出，再执行本脚本。"
}

Write-Host '清理构建产物...' -ForegroundColor Cyan
Remove-BuildArtifacts

if ($Clean) {
    Write-Host '清理完成（-Clean 未执行打包）' -ForegroundColor Green
    return
}

# 框架依赖单文件：不含 .NET 运行时，但把托管依赖、原生库与 data 资源一并塞进 exe
$publishArgs = @(
    $project
    '-c', 'Release'
    '-r', 'win-x64'
    '--self-contained', 'false'
    '-p:PublishSingleFile=true'
    '-p:IncludeNativeLibrariesForSelfExtract=true'
    '-p:IncludeAllContentForSelfExtract=true'
)

Write-Host '开始打包...' -ForegroundColor Cyan
& dotnet publish @publishArgs -o $outDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish 失败，退出码 $LASTEXITCODE" }

# 发布产物已落在 $outDir，bin/obj 只是编译中间产物，丢掉保持仓库干净
Remove-Path (Join-Path $root 'bin')
Remove-Path (Join-Path $root 'obj')

$exe = Join-Path $outDir 'NotchPeninsula.exe'
if (-not (Test-Path $exe)) { throw "未找到发布产物：$exe" }

# 插件不参与单文件打包（PluginLoader 从 exe 同级的 plugins\ 目录加载），需随发布产物一起部署
$pluginsSrc = Join-Path $root 'plugins'
$pluginsDst = Join-Path $outDir 'plugins'
if (Test-Path $pluginsSrc) {
    Remove-Path $pluginsDst
    Copy-Item -Recurse -Force $pluginsSrc $pluginsDst
    Write-Host "已复制插件目录：$pluginsDst" -ForegroundColor Cyan
} else {
    Write-Host '未找到 plugins 目录，发布产物将不含任何插件。' -ForegroundColor Yellow
}

$sizeMb = [math]::Round((Get-Item $exe).Length / 1MB, 2)
Write-Host ''
Write-Host "打包完成：$exe（$sizeMb MB）" -ForegroundColor Green
Write-Host '提示：未打包 .NET 运行时，目标机需已安装 .NET 10 Desktop Runtime。' -ForegroundColor Yellow
Write-Host "提示：分发时需把 $outDir\plugins 与 exe 放在同一目录，否则插件不会加载。" -ForegroundColor Yellow
