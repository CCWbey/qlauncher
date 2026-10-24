<#
    QLaunch 一键构建：关闭本目录下的运行实例 -> publish 到 dist -> 可选无头自检 -> 启动。
    只需双击同目录的 build.cmd，或在本目录执行 .\build.ps1。
#>
[CmdletBinding()]
param(
    [switch]$NoRun,
    [switch]$SelfTest
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$Root = $PSScriptRoot
$Project = Join-Path $Root 'QLaunch.csproj'
$OutDir = Join-Path $Root 'dist'
$Exe = Join-Path $OutDir 'QLaunch.exe'
$ProcessName = 'QLaunch'

function Resolve-DotNetWithSdk
{
    # PATH 上的 dotnet 可能只是运行时，逐个候选确认能列出 SDK 才算可用。
    $candidates = @()
    $fromPath = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($fromPath) { $candidates += $fromPath.Source }
    $candidates += (Join-Path $env:LOCALAPPDATA 'dotnet\dotnet.exe')
    $candidates += 'C:\Program Files\dotnet\dotnet.exe'

    foreach ($cand in $candidates)
    {
        if (-not (Test-Path -LiteralPath $cand)) { continue }
        $sdks = & $cand --list-sdks 2>$null | Where-Object { $_ -and $_.Trim().Length -gt 0 }
        if ($sdks)
        {
            Write-Host "使用 SDK：$cand（$(($sdks | Select-Object -First 1).Trim())）"
            return $cand
        }
    }
    throw '没找到带 .NET 10 SDK 的 dotnet。已依次尝试 PATH、%LOCALAPPDATA%\dotnet、C:\Program Files\dotnet；请安装 SDK，或把装有 SDK 的 dotnet 目录放到 PATH 最前面。'
}

function Get-ProcessPath($Proc)
{
    # 32/64 位不匹配、或对方是提权进程时读 Path 会抛，读不到就当作"不属于本项目"处理。
    try { return $Proc.Path } catch { return $null }
}

function Stop-ProjectInstance
{
    $all = @(Get-Process -Name $ProcessName -ErrorAction SilentlyContinue)
    if ($all.Count -eq 0)
    {
        Write-Host '没有正在运行的 QLaunch 实例。'
        return
    }

    $mine = @($all | Where-Object { $p = Get-ProcessPath $_; $p -and $p.StartsWith($Root + '\', 'OrdinalIgnoreCase') })
    $others = @($all | Where-Object { $p = Get-ProcessPath $_; -not ($p -and $p.StartsWith($Root + '\', 'OrdinalIgnoreCase')) })

    # 只结束本仓库里的 exe：同名进程可能来自别处的另一份构建，不能顺手杀掉。
    if ($others.Count -gt 0)
    {
        $paths = ($others | ForEach-Object { Get-ProcessPath $_ } | Where-Object { $_ }) -join '; '
        Write-Warning "跳过不属于本项目的 $ProcessName 进程（PID $($others.Id -join ', ')）：$paths"
    }
    if ($mine.Count -eq 0) { return }

    foreach ($proc in $mine)
    {
        Write-Host "关闭运行中的实例：PID $($proc.Id) $((Get-ProcessPath $proc))"
        # 托盘程序只有右键菜单"退出"才会结束进程，没有可关闭的主窗口，只能直接结束；
        # 设置是在每次启动应用时即时写盘的，索引缓存也只在重扫时写，杀掉不丢数据。
        $proc.Kill()
        if (-not $proc.WaitForExit(5000))
        {
            throw "PID $($proc.Id) 关闭后仍未退出，dist 下的文件可能被锁住。请手动结束后重试。"
        }
    }
    # 单实例互斥量在进程退出时才释放，稍等一下避免下一步启动被判为重复实例。
    Start-Sleep -Milliseconds 500
}

$dotnet = Resolve-DotNetWithSdk
Stop-ProjectInstance

Write-Host "构建中：dotnet publish -c Release -o dist"
& $dotnet publish $Project -c Release -o $OutDir --nologo -v minimal
if ($LASTEXITCODE -ne 0) { throw "构建失败，退出码 $LASTEXITCODE。" }
if (-not (Test-Path -LiteralPath $Exe)) { throw "构建成功但找不到 $Exe。" }

$built = Get-Item -LiteralPath $Exe
Write-Host "产物：$Exe（$([math]::Round($built.Length / 1KB)) KB，$($built.LastWriteTime.ToString('HH:mm:ss'))）"

if ($SelfTest)
{
    # 自检不开窗口，索引/Everything/翻译三条链路的结果写 %APPDATA%\QLaunch\selftest.log。
    $log = Join-Path $env:APPDATA 'QLaunch\selftest.log'
    Remove-Item -LiteralPath $log -ErrorAction SilentlyContinue
    Write-Host '无头自检中（翻译模型预热最长约 30 秒）…'
    $check = Start-Process -FilePath $Exe -WorkingDirectory $OutDir -ArgumentList '--selftest', 'hello world' -PassThru
    $deadline = (Get-Date).AddSeconds(75)
    while (-not $check.HasExited -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 500 }
    if (-not $check.HasExited) { $check.Kill(); throw '自检超时未完成。' }
    # 有实例在跑时 --selftest 会抢不到互斥量直接静默退出，日志也就不会新生成。
    if (-not (Test-Path -LiteralPath $log)) { throw "自检进程没写出 $log（可能有其它 QLaunch 实例在跑）。" }
    Get-Content -LiteralPath $log -Encoding UTF8
    $text = Get-Content -LiteralPath $log -Raw -Encoding UTF8
    if ($text -notmatch 'translator status=ready') { throw '自检未通过：翻译引擎没到 ready，见上面 selftest.log。' }
    if ($text -notmatch '(?m)^translate \[Translated\]: \S') { throw '自检未通过：translate 一行不是成功译文，见上面 selftest.log。' }
    Write-Host '自检通过：索引、Everything、翻译三条链路都有结果。'
}

if ($NoRun)
{
    Write-Host '按 -NoRun 要求不启动实例。'
    return
}

# 工作目录设成 dist，运行期落在当前目录的临时文件才不会散进源码树。
Start-Process -FilePath $Exe -WorkingDirectory $OutDir
Write-Host '已启动，按 Alt+Space 唤出搜索框。'
