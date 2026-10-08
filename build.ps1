#Requires -Version 5.1
<#
.SYNOPSIS
    NotchPeninsula 打包脚本：清理构建产物，发布为不打包运行时的单文件 exe。

.DESCRIPTION
    不打包 .NET 运行时（目标机需自备 .NET 10 Desktop Runtime，否则会提示 "You must install .NET"），
    只把程序自身、依赖 dll（NAudio / SkiaSharp / WinRT 等）和资源文件
    （data 图片、vcruntime140.dll、msvcp140.dll 等）全部合并进单个 exe，
    直接输出到 publish 目录，产物约 35 MB。

    脚本会自动定位一个**带 SDK** 的 dotnet 主机（只有 Runtime 的安装不算），
    查找顺序见 -DotnetPath 参数说明。

.PARAMETER Clean
    仅清理构建产物，不执行打包。

.PARAMETER Output
    发布输出目录（相对仓库根目录），默认 publish。

.PARAMETER DotnetPath
    显式指定 dotnet.exe 的完整路径（其旁必须存在 sdk 目录）。
    未指定时依次尝试：环境变量 NOTCHPENINSULA_DOTNET → DOTNET_ROOT →
    仓库内 .tools\dotnet-sdk\dotnet.exe → %ProgramFiles%\dotnet\dotnet.exe → PATH 中的 dotnet。

.PARAMETER Force
    即使检测到 NotchPeninsula 正在运行也继续执行（产物可能因文件被占用而写入失败）。

.EXAMPLE
    .\build.ps1
    清理后打包，产出 publish\NotchPeninsula.exe（约 35 MB）。

.EXAMPLE
    .\build.ps1 -Clean
    只清理 bin/obj/publish，不打包。

.EXAMPLE
    .\build.ps1 -DotnetPath 'D:\dotnet-sdk\dotnet.exe'
    用指定的 SDK 打包。
#>
[CmdletBinding()]
param(
    [switch]$Clean,

    [string]$Output = 'publish',

    [string]$DotnetPath = '',

    [switch]$Force
)

$ErrorActionPreference = 'Stop'

$root = $PSScriptRoot
$project = Join-Path $root 'NotchPeninsula.csproj'
$outDir = Join-Path $root $Output

# ---------------------------------------------------------------- 辅助函数

function Remove-PathSafe([string]$path) {
    if (-not (Test-Path -LiteralPath $path)) { return }
    try {
        Remove-Item -LiteralPath $path -Recurse -Force -ErrorAction Stop
        Write-Host "  已删除 $path" -ForegroundColor DarkGray
    } catch {
        # 清理属于「尽力而为」：bin / obj 下可能仍有编译进程（VBCSCompiler、MSBuild 节点）
        # 持有的句柄，某些环境对批量删除另有额外限制 —— 失败不该让整轮打包作废，
        # 提示原因后继续，后续 publish 会自行覆盖所需文件。
        Write-Host "  跳过 $path：$($_.Exception.Message)" -ForegroundColor Yellow
    }
}

function Test-DotnetHasSdk([string]$exe) {
    if ([string]::IsNullOrWhiteSpace($exe)) { return $false }
    if (-not (Test-Path -LiteralPath $exe)) { return $false }
    try {
        $sdks = & $exe --list-sdks 2>$null
    } catch {
        return $false
    }
    # 只有运行时的安装会返回空 —— 那种 dotnet 执行 publish 会直接报「找不到命令」
    return [bool]($sdks | Where-Object { $_ -match '^\d+\.\d+\.' })
}

function Resolve-DotnetHost([string]$explicit) {
    $candidates = New-Object System.Collections.Generic.List[string]

    if (-not [string]::IsNullOrWhiteSpace($explicit)) { $candidates.Add($explicit) }
    if ($env:NOTCHPENINSULA_DOTNET) { $candidates.Add($env:NOTCHPENINSULA_DOTNET) }
    if ($env:DOTNET_ROOT) { $candidates.Add((Join-Path $env:DOTNET_ROOT 'dotnet.exe')) }
    # 仓库内约定：本地 SDK 放这里即可（.tools 已在 .gitignore 中排除）
    $candidates.Add((Join-Path $root '.tools\dotnet-sdk\dotnet.exe'))
    if ($env:ProgramFiles) { $candidates.Add((Join-Path $env:ProgramFiles 'dotnet\dotnet.exe')) }
    $onPath = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($onPath) { $candidates.Add($onPath.Source) }

    foreach ($candidate in ($candidates | Select-Object -Unique)) {
        if (Test-DotnetHasSdk $candidate) { return $candidate }
    }
    return ''
}

# ---------------------------------------------------------------- 主流程

try {
    $dotnet = Resolve-DotnetHost $DotnetPath
    if (-not $dotnet) {
        throw @'
找不到带 SDK 的 dotnet。

本机可能只装了 .NET Runtime / Desktop Runtime —— 那种安装没有 sdk 目录，
执行 dotnet publish 会失败。请任选一种方式提供 SDK：

  1. 运行参数          .\build.ps1 -DotnetPath "D:\dotnet-sdk\dotnet.exe"
  2. 用户环境变量      setx NOTCHPENINSULA_DOTNET "D:\dotnet-sdk\dotnet.exe"
  3. 用户环境变量      setx DOTNET_ROOT "D:\dotnet-sdk"
  4. 仓库内约定        把 SDK 放到 <仓库根>\.tools\dotnet-sdk\dotnet.exe
  5. 官方安装位置      %ProgramFiles%\dotnet\dotnet.exe（该目录下需有 sdk 子目录）

自检方法：dotnet --list-sdks 至少应列出 10.0.x 一个版本；输出为空即表示没有 SDK。
'@
    }

    Write-Host "dotnet：$dotnet" -ForegroundColor DarkGray
    Write-Host ("SDK：" + ((& $dotnet --list-sdks | Select-Object -First 1))) -ForegroundColor DarkGray

    # 本程序常驻托盘，从 bin 下运行时会把产物锁住，导致清理失败
    $running = @(Get-Process -Name 'NotchPeninsula' -ErrorAction SilentlyContinue)
    if ($running.Count -gt 0) {
        if (-not $Force) {
            throw ("检测到 NotchPeninsula 正在运行（PID: " + ($running.Id -join ', ') + "）。`n" +
                   "程序常驻托盘会锁住 bin / publish 下的文件，导致清理与覆盖失败。`n" +
                   "请先从托盘菜单退出，或改用：.\build.ps1 -Force")
        }
        Write-Host "NotchPeninsula 正在运行，已按 -Force 继续执行。" -ForegroundColor Yellow
    }

    Write-Host '清理构建产物...' -ForegroundColor Cyan
    Remove-PathSafe (Join-Path $root 'bin')
    Remove-PathSafe (Join-Path $root 'obj')
    Remove-PathSafe $outDir

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
    & $dotnet publish @publishArgs -o $outDir
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish 失败，退出码 $LASTEXITCODE" }

    # 发布产物已落在 $outDir，bin/obj 只是编译中间产物，丢掉保持仓库干净
    Remove-PathSafe (Join-Path $root 'bin')
    Remove-PathSafe (Join-Path $root 'obj')

    $exe = Join-Path $outDir 'NotchPeninsula.exe'
    if (-not (Test-Path -LiteralPath $exe)) { throw "未找到发布产物：$exe" }

    $sizeMb = [math]::Round((Get-Item -LiteralPath $exe).Length / 1MB, 2)
    Write-Host ''
    Write-Host "打包完成：$exe（$sizeMb MB）" -ForegroundColor Green
    Write-Host '提示：未打包 .NET 运行时，目标机需已安装 .NET 10 Desktop Runtime。' -ForegroundColor Yellow
    exit 0
}
catch {
    Write-Host ''
    Write-Host $_.Exception.Message -ForegroundColor Red
    # 双击运行（右键「使用 PowerShell 运行」）时窗口会随进程结束立刻关闭，
    # 这里停一下让报错可见；管道 / CI 等非交互场景不阻塞。
    if ($Host.Name -eq 'ConsoleHost' -and -not [Console]::IsInputRedirected) {
        Write-Host ''
        Read-Host '按回车键退出'
    }
    exit 1
}
