# PICO Business Suite 服务模式一键管理工具
param(
    [string]$Mode = "Interactive", # "Interactive", "Enable", "Disable", "Status", "Restart"
    [switch]$NoRestart
)

$ErrorActionPreference = "Continue"

function Get-PicoIniPath {
    # 1. 当前用户 AppData
    $localApp = $env:LOCALAPPDATA
    $p1 = Join-Path $localApp "PICOBusinessSuitData\key-setting.ini"
    if (Test-Path $p1) { return $p1 }

    $p2 = Join-Path $localApp "PICOBusinessSuitData\setting.ini"
    if (Test-Path $p2) { return $p2 }

    # 2. 检查 C:\Users\ 下其他用户
    $users = Get-ChildItem "C:\Users" -Directory -ErrorAction SilentlyContinue
    foreach ($u in $users) {
        $cand1 = Join-Path $u.FullName "AppData\Local\PICOBusinessSuitData\key-setting.ini"
        if (Test-Path $cand1) { return $cand1 }
        $cand2 = Join-Path $u.FullName "AppData\Local\PICOBusinessSuitData\setting.ini"
        if (Test-Path $cand2) { return $cand2 }
    }

    # 3. 安装目录 fallback
    $installs = @(
        "D:\Program Files (x86)\PICO Business Suite\setting.ini",
        "C:\Program Files (x86)\PICO Business Suite\setting.ini",
        "C:\Program Files\PICO Business Suite\setting.ini"
    )
    foreach ($inst in $installs) {
        if (Test-Path $inst) { return $inst }
    }

    # 默认当前用户的 key-setting.ini 路径
    return $p1
}

function Read-PicoServiceStatus {
    $iniPath = Get-PicoIniPath
    $exists = Test-Path $iniPath
    $runAsService = $false
    $sectionFound = $false

    if ($exists) {
        $lines = [System.IO.File]::ReadAllLines($iniPath, [System.Text.Encoding]::UTF8)
        $inService = $false
        foreach ($line in $lines) {
            $trimmed = $line.Trim()
            if ($trimmed.StartsWith("[") -and $trimmed.EndsWith("]")) {
                $inService = ($trimmed -ieq "[Service]")
                if ($inService) { $sectionFound = $true }
                continue
            }
            if ($inService -and $trimmed -match "^runAsService\s*=\s*(.*)$") {
                $val = $matches[1].Trim().ToLowerInvariant()
                $runAsService = ($val -eq "true" -or $val -eq "1")
            }
        }
    }

    $procs = Get-Process -Name "PICOBusinessSuite" -ErrorAction SilentlyContinue
    $isRunning = ($procs -ne $null -and $procs.Count -gt 0)
    $pidStr = if ($isRunning) { ($procs | ForEach-Object { $_.Id }) -join ", " } else { "无" }
    $procPath = if ($isRunning) { $procs[0].Path } else { "" }

    return [PSCustomObject]@{
        IniPath = $iniPath
        Exists = $exists
        RunAsService = $runAsService
        SectionFound = $sectionFound
        IsRunning = $isRunning
        ProcessId = $pidStr
        ProcessPath = $procPath
    }
}

function Set-PicoServiceModeInternal {
    param([bool]$Enable, [bool]$Restart = $true)
    $iniPath = Get-PicoIniPath
    $dir = [System.IO.Path]::GetDirectoryName($iniPath)
    if (-not (Test-Path $dir)) {
        New-Item -ItemType Directory -Path $dir -Force | Out-Null
    }

    # 备份
    if (Test-Path $iniPath) {
        Copy-Item -Path $iniPath -Destination "$iniPath.bak" -Force
        Write-Host "  [已备份原始配置] -> $iniPath.bak" -ForegroundColor DarkGray
    }

    $lines = if (Test-Path $iniPath) { [System.IO.File]::ReadAllLines($iniPath, [System.Text.Encoding]::UTF8) } else { @() }
    $newLines = [System.Collections.Generic.List[string]]::new()
    $inService = $false
    $sectionFound = $false
    $keyFound = $false
    $valStr = if ($Enable) { "true" } else { "false" }

    foreach ($line in $lines) {
        $trimmed = $line.Trim()
        if ($trimmed.StartsWith("[") -and $trimmed.EndsWith("]")) {
            if ($inService -and -not $keyFound) {
                $newLines.Add("runAsService=$valStr")
                $keyFound = $true
            }
            $inService = ($trimmed -ieq "[Service]")
            if ($inService) { $sectionFound = $true }
            $newLines.Add($line)
            continue
        }

        if ($inService -and $trimmed.StartsWith("runAsService", [System.StringComparison]::OrdinalIgnoreCase)) {
            $newLines.Add("runAsService=$valStr")
            $keyFound = $true
            continue
        }

        $newLines.Add($line)
    }

    if ($inService -and -not $keyFound) {
        $newLines.Add("runAsService=$valStr")
        $keyFound = $true
    }

    if (-not $sectionFound) {
        if ($newLines.Count -gt 0 -and $newLines[$newLines.Count - 1].Trim() -ne "") {
            $newLines.Add("")
        }
        $newLines.Add("[Service]")
        $newLines.Add("runAsService=$valStr")
    }

    [System.IO.File]::WriteAllLines($iniPath, $newLines, [System.Text.UTF8Encoding]::new($false))

    Write-Host "  [配置修改成功] -> 已写入: runAsService=$valStr" -ForegroundColor Green
    Write-Host "  [文件路径] -> $iniPath" -ForegroundColor Cyan

    if ($Restart) {
        Restart-PicoSuite
    }
}

function Restart-PicoSuite {
    Write-Host "  [正在检查 PICO 企业服务客户端进程]..." -ForegroundColor Yellow
    $procs = Get-Process -Name "PICOBusinessSuite" -ErrorAction SilentlyContinue
    $exePath = ""

    if ($procs) {
        foreach ($p in $procs) {
            try {
                if (-not $exePath) { $exePath = $p.Path }
                Stop-Process -Id $p.Id -Force
            } catch {}
        }
        Write-Host "  [已关闭旧进程] 等待 1 秒重启..." -ForegroundColor DarkYellow
        Start-Sleep -Milliseconds 1000
    }

    if (-not $exePath) {
        $candidates = @(
            "D:\Program Files (x86)\PICO Business Suite\PICOBusinessSuite.exe",
            "C:\Program Files (x86)\PICO Business Suite\PICOBusinessSuite.exe",
            "C:\Program Files\PICO Business Suite\PICOBusinessSuite.exe"
        )
        foreach ($c in $candidates) {
            if (Test-Path $c) { $exePath = $c; break }
        }
    }

    if ($exePath -and (Test-Path $exePath)) {
        Start-Process -FilePath $exePath -WorkingDirectory ([System.IO.Path]::GetDirectoryName($exePath))
        Write-Host "  [重启完成] PICO 企业服务客户端已成功启动！" -ForegroundColor Green
    } else {
        Write-Host "  [提示] 未能自动定位 PICOBusinessSuite.exe 安装路径，请手动打开客户端以载入新服务模式。" -ForegroundColor Yellow
    }
}

# ----------------- 执行分发 -----------------
if ($Mode -ieq "Enable") {
    Set-PicoServiceModeInternal -Enable $true -Restart (-not $NoRestart)
    exit 0
}
if ($Mode -ieq "Disable") {
    Set-PicoServiceModeInternal -Enable $false -Restart (-not $NoRestart)
    exit 0
}
if ($Mode -ieq "Restart") {
    Restart-PicoSuite
    exit 0
}
if ($Mode -ieq "Status") {
    $st = Read-PicoServiceStatus
    Write-Host ($st | ConvertTo-Json)
    exit 0
}

# ----------------- 交互菜单 (Interactive) -----------------
while ($true) {
    Clear-Host
    $st = Read-PicoServiceStatus

    Write-Host "====================================================================" -ForegroundColor Cyan
    Write-Host "          PICO Business Suite 服务模式一键切换与检测工具             " -ForegroundColor Cyan
    Write-Host "====================================================================" -ForegroundColor Cyan
    Write-Host ""
    Write-Host "【当前配置与状态】" -ForegroundColor Yellow
    Write-Host "  • 配置文件路径 : " -NoNewline; Write-Host $st.IniPath -ForegroundColor Gray
    Write-Host "  • 配置文件存在 : " -NoNewline
    if ($st.Exists) { Write-Host "[存在]" -ForegroundColor Green } else { Write-Host "[尚未创建/默认]" -ForegroundColor Yellow }

    Write-Host "  • 服务模式状态 : " -NoNewline
    if ($st.RunAsService) {
        Write-Host "🟢 [已开启服务模式 (runAsService=true)]" -ForegroundColor Green
    } else {
        Write-Host "🔴 [未开启服务模式 (runAsService=false)]" -ForegroundColor Red
    }

    Write-Host "  • 企业服务进程 : " -NoNewline
    if ($st.IsRunning) {
        Write-Host "🟢 [正在运行 (PID: $($st.ProcessId))]" -ForegroundColor Green
    } else {
        Write-Host "⚪ [未运行]" -ForegroundColor DarkGray
    }
    Write-Host ""
    Write-Host "--------------------------------------------------------------------" -ForegroundColor DarkGray
    Write-Host "请选择操作：" -ForegroundColor White
    Write-Host "  [1] 一键开启服务模式 (设置 runAsService=true 并自动重启客户端)" -ForegroundColor Green
    Write-Host "  [2] 一键关闭服务模式 (恢复 runAsService=false)" -ForegroundColor Yellow
    Write-Host "  [3] 重启 PICO 企业服务客户端 (PICOBusinessSuite.exe)" -ForegroundColor Cyan
    Write-Host "  [4] 打开配置文件所在文件夹" -ForegroundColor Gray
    Write-Host "  [5] 刷新当前检测状态" -ForegroundColor White
    Write-Host "  [0] 退出工具" -ForegroundColor White
    Write-Host "--------------------------------------------------------------------" -ForegroundColor DarkGray
    $choice = Read-Host "请输入选项编号 [0-5]"

    switch ($choice) {
        "1" {
            Write-Host ""
            Write-Host ">>> 正在执行开启服务模式..." -ForegroundColor Cyan
            Set-PicoServiceModeInternal -Enable $true -Restart $true
            Write-Host ""
            Write-Host "操作完成！按任意键返回菜单..." -ForegroundColor Green
            [void][System.Console]::ReadKey()
        }
        "2" {
            Write-Host ""
            Write-Host ">>> 正在执行关闭服务模式..." -ForegroundColor Yellow
            Set-PicoServiceModeInternal -Enable $false -Restart $true
            Write-Host ""
            Write-Host "操作完成！按任意键返回菜单..." -ForegroundColor Green
            [void][System.Console]::ReadKey()
        }
        "3" {
            Write-Host ""
            Restart-PicoSuite
            Write-Host ""
            Write-Host "操作完成！按任意键返回菜单..." -ForegroundColor Green
            [void][System.Console]::ReadKey()
        }
        "4" {
            $dir = [System.IO.Path]::GetDirectoryName($st.IniPath)
            if (Test-Path $dir) {
                Start-Process explorer.exe $dir
            } else {
                Write-Host "目录尚未创建: $dir" -ForegroundColor Yellow
                Start-Sleep -Seconds 1
            }
        }
        "5" {
            # 自动刷新
        }
        "0" {
            exit 0
        }
        default {
            Write-Host "无效选项，请重新输入" -ForegroundColor Red
            Start-Sleep -Milliseconds 800
        }
    }
}
