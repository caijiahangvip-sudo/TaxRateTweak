#Requires -Version 7.0
<#
    TaxRateTweak（Cities: Skylines II）一键发布脚本 —— PowerShell 7

    用法
    ====
        pwsh -File scripts/release.ps1 -Version 1.8.29 -ChangeLog "本版修了……" -DryRun
        pwsh -File scripts/release.ps1 -Version 1.8.29 -ChangeLog "本版修了……"
        pwsh -File scripts/release.ps1 -Version 1.8.29 -ChangeLog "本版修了……" -SkipPublish -Commit -NoPush

    参数
    ====
        -Version <x.y.z>     必填。新版本号，形如 1.8.29。必须严格大于
                             Properties/PublishConfiguration.xml 中当前的 ModVersion。
        -ChangeLog <文本>     必填。本版本变更日志正文，**不含** "v1.8.29: " 前缀
                             （脚本自动补）。只写当前这一版，不要拼接历史版本。
        -DryRun              只做校验与打印：不写任何文件、不构建、不发布、不提交。
        -SkipPublish         完成版本号改写、变更日志改写、构建（以及可选的提交），但不发布。
        -NoPush              配合 -Commit 使用：只做提交与打 tag，不推送到远端。
        -Commit              开关，默认关闭。开启后才执行 git add / commit / tag（以及推送）。

    退出码
    ======
        0     全部成功。
        非 0  任一步失败。特别注意：发布器成功但线上页面在重试窗口内仍未刷新时，
              也以非 0 退出，提醒人工确认。

    注意（-DryRun 的边界）
    ======================
        -DryRun 只做只读检查：`node --check` 会执行（不产生文件），但
        `dotnet restore` / `dotnet build` 会被跳过，因为它们必然写入
        obj/、bin/ 以及 pdx-staging/TaxRateTweak，违反"不写任何文件"的约束。
        DryRun 会打印真实运行时会执行的确切命令。

    平台硬限制（超了会被服务端拒收）
    ================================
        LongDescription  内容长度 1..20000 字符
        ChangeLog        内容长度 1..5000  字符
        单张截图文件     <= 2.1 MB
#>

[CmdletBinding()]
param(
    # AllowEmptyString：让空值走到脚本自己的中文校验分支，而不是 PowerShell 的参数绑定错误。
    [Parameter(Mandatory = $true, HelpMessage = '新版本号，形如 1.8.29')]
    [AllowEmptyString()]
    [string]$Version,

    [Parameter(Mandatory = $true, HelpMessage = '本版本变更日志正文，不含 "v1.8.29: " 前缀')]
    [AllowEmptyString()]
    [string]$ChangeLog,

    [switch]$DryRun,
    [switch]$SkipPublish,
    [switch]$NoPush,
    [switch]$Commit
)

$ErrorActionPreference = 'Stop'
# 原生命令写到 stderr 的内容（git push 进度、dotnet 提示）不应被当成终止性错误。
$PSNativeCommandUseErrorActionPreference = $false

# ============================================================================
# 常量
# ============================================================================
$RepoRoot      = 'C:\cs2-mod-factory'
$ModDir        = 'C:\cs2-mod-factory\pdx-mods\TaxRateTweak'
$StagingDir    = 'C:\cs2-mod-factory\pdx-staging\TaxRateTweak'
$StagingDirCmd = 'C:/cs2-mod-factory/pdx-staging/TaxRateTweak'   # 传给发布器时用正斜杠
$ConfigRel     = 'Properties\PublishConfiguration.xml'
$ConfigPath    = Join-Path $ModDir $ConfigRel
$GitBranch     = 'master'

$PublisherExe = 'D:\Steam\steamapps\common\Cities Skylines II\Cities2_Data\Content\Game\.ModdingToolchain\ModPublisher\ModPublisher.exe'
$ChromeExe    = 'C:\Program Files\Google\Chrome\Application\chrome.exe'
$StoreUrl     = 'https://mods.paradoxplaza.com/mods/155720/Windows'

$MaxLongDescriptionLen = 20000
$MaxChangeLogLen       = 5000
$MaxScreenshotBytes    = [long](2.1 * 1MB)

# 需要同步版本号的源文件（XML 配置单独处理）
$SourceVersionFiles = @(
    'Systems\DensityDemandUISystem.cs',
    'Systems\FiveDensityDemandSystem.cs',
    'UI\TaxRateTweak.mjs'
)

# 写版本号之后必须逐处回读到的新值模板（{0} = 新版本号）
$VersionProbes = @(
    @{ Rel = 'Systems\DensityDemandUISystem.cs';   Probe = '"TaxRateTweak {0}: five-density UI binding registered."'; Desc = 'DensityDemandUISystem 日志串' }
    @{ Rel = 'Systems\FiveDensityDemandSystem.cs'; Probe = 'Five-density demand {0} ready:';                                    Desc = 'FiveDensityDemandSystem 日志串' }
    @{ Rel = 'UI\TaxRateTweak.mjs';                Probe = '* Version: {0}';                                                    Desc = 'mjs 注释头 Version' }
    @{ Rel = 'UI\TaxRateTweak.mjs';                Probe = 'console.info("TaxRateTweak {0}: five residential demand sections'; Desc = 'mjs console.info 日志串' }
)

# 发布目录必需文件
$RequiredStagedFiles = @(
    'TaxRateTweak.dll'
    'TaxRateTweak.mjs'
    '0Harmony.dll'
    'Mono.Cecil.dll'
    'MonoMod.Utils.dll'
    'MonoMod.RuntimeDetour.dll'
    'THIRD_PARTY_NOTICES.txt'
)

$PublisherOkMarker    = 'Publishing new version updating process finished successfully'
$PublisherFailPrefix  = 'Could not publish'

$GitMaxRetries        = 4      # 推送：最多重试 4 次
$PublishMaxRetries    = 4      # 发布器：最多重试 4 次
$PageCheckMaxRetries  = 6      # 页面回读：最多重试 6 次
$RetryDelaySeconds    = 8
$PageCheckDelaySec    = 60

# ============================================================================
# 输出helpers
# ============================================================================
function Write-Head {
    param([string]$Text)
    Write-Host ''
    Write-Host ('=' * 74) -ForegroundColor DarkCyan
    Write-Host "  $Text" -ForegroundColor Cyan
    Write-Host ('=' * 74) -ForegroundColor DarkCyan
}
function Write-Sub  { param([string]$T) Write-Host "  · $T" -ForegroundColor Gray }
function Write-Kv   { param([string]$K, [string]$V) Write-Host ("  {0,-16}: {1}" -f $K, $V) }
function Write-Ok   { param([string]$T) Write-Host "  [OK]   $T" -ForegroundColor Green }
function Write-Warn { param([string]$T) Write-Host "  [警告] $T" -ForegroundColor Yellow }
function Write-Err  { param([string]$T) Write-Host "  [错误] $T" -ForegroundColor Red }

function Stop-Failure {
    param([string]$Message)
    Write-Host ''
    Write-Host "[失败] $Message" -ForegroundColor Red
    Write-Host ''
    exit 1
}

function Format-FileSize {
    param([long]$Bytes)
    if ($Bytes -ge 1MB) { return ('{0:N2} MB ({1:N0} 字节)' -f ($Bytes / 1MB), $Bytes) }
    if ($Bytes -ge 1KB) { return ('{0:N2} KB ({1:N0} 字节)' -f ($Bytes / 1KB), $Bytes) }
    return ("$Bytes 字节")
}

# ============================================================================
# 通用工具
# ============================================================================
function Get-LineNumberAt {
    param([string]$Text, [int]$Index)
    if ($Index -le 0) { return 1 }
    return ([regex]::Matches($Text.Substring(0, $Index), "`n")).Count + 1
}

# 在 .NET 正则"替换串"里 $ 有特殊含义，逐字面替换前先转义。
function ConvertTo-RegexReplacement {
    param([string]$Text)
    return ($Text -replace '\$', '$$')
}

function ConvertTo-XmlEscaped {
    param([string]$Text)
    return $Text.Replace('&', '&amp;').Replace('<', '&lt;').Replace('>', '&gt;')
}

# 统一用 UTF-8 无 BOM 写回：现有文件都是无 BOM + LF，字符串里的 LF 会被原样保留。
function Write-TextFileNoBom {
    param([string]$Path, [string]$Text)
    $enc = New-Object System.Text.UTF8Encoding($false)
    [System.IO.File]::WriteAllText($Path, $Text, $enc)
}

# 只读 PNG 的 IHDR 头拿到宽高，不引入任何图像库。
function Get-PngDimensions {
    param([string]$Path)
    $fs = [System.IO.File]::OpenRead($Path)
    try {
        $buf = New-Object byte[] 24
        $read = $fs.Read($buf, 0, 24)
        if ($read -lt 24) { return $null }
        if (-not ($buf[0] -eq 0x89 -and $buf[1] -eq 0x50 -and $buf[2] -eq 0x4E -and $buf[3] -eq 0x47)) { return $null }
        $w = [int]$buf[16] * 16777216 + [int]$buf[17] * 65536 + [int]$buf[18] * 256 + [int]$buf[19]
        $h = [int]$buf[20] * 16777216 + [int]$buf[21] * 65536 + [int]$buf[22] * 256 + [int]$buf[23]
        return [pscustomobject]@{ Width = $w; Height = $h }
    }
    finally { $fs.Dispose() }
}

# 捕获原生命令的 stdout+stderr 与退出码（不抛异常，由调用方判定）
function Invoke-NativeCapture {
    param(
        [Parameter(Mandatory = $true)][string]$FilePath,
        [string[]]$Arguments = @(),
        [string]$WorkingDirectory
    )
    $savedDir = $null
    if ($WorkingDirectory) {
        $savedDir = (Get-Location).Path
        Set-Location -LiteralPath $WorkingDirectory
    }
    try {
        $raw = & $FilePath @Arguments 2>&1
        $code = $LASTEXITCODE
    }
    finally {
        if ($savedDir) { Set-Location -LiteralPath $savedDir }
    }
    $lines = @()
    if ($null -ne $raw) {
        $lines = @($raw | ForEach-Object {
            if ($_ -is [System.Management.Automation.ErrorRecord]) { $_.ToString() } else { [string]$_ }
        })
    }
    return [pscustomobject]@{
        ExitCode = $code
        Lines    = $lines
        Text     = ($lines -join "`n")
    }
}

# 构建输出里的"警告/错误"行；"0 个警告 / 0 个错误 / 0 Warning(s) / 0 Error(s)"不算问题。
function Get-BuildProblemLines {
    param([string[]]$Lines)
    $bad = New-Object System.Collections.Generic.List[string]
    foreach ($l in $Lines) {
        $s = $l
        $s = [regex]::Replace($s, '(?i)\b0\s*warnings?\(s\)', '')
        $s = [regex]::Replace($s, '(?i)\b0\s*errors?\(s\)', '')
        $s = [regex]::Replace($s, '0\s*个警告', '')
        $s = [regex]::Replace($s, '0\s*个错误', '')
        if ($s -match '(?i)\bwarning\b|\berror\b|警告|错误') { $bad.Add($l.Trim()) }
    }
    return $bad
}

function Get-FirstSentence {
    param([string]$Text)
    $t = ($Text -split "(`r`n|`n|`r)")[0].Trim()
    $m = [regex]::Match($t, '^[^。．.!?！？]*[。．.!?！？]')
    if ($m.Success) { $t = $m.Value }
    $t = $t.TrimEnd('。', '．', '.', '!', '！', '?', '？').Trim()
    if ($t.Length -gt 80) { $t = $t.Substring(0, 80) + '…' }
    return $t
}

# 解析发布配置文本（纯只读）。DryRun 下也用来校验"模拟写完之后的 XML 是否仍然是合法 XML"。
function ConvertFrom-PublishXmlText {
    param([Parameter(Mandatory = $true)][string]$Raw)

    $doc = New-Object System.Xml.XmlDocument
    $doc.LoadXml($Raw)
    $root = $doc.DocumentElement

    $nVer   = $root.SelectSingleNode('ModVersion')
    $nLong  = $root.SelectSingleNode('LongDescription')
    $nLog   = $root.SelectSingleNode('ChangeLog')
    $nAcc   = $root.SelectSingleNode('AccessLevel')
    $nThumb = $root.SelectSingleNode('Thumbnail')

    $ver      = if ($nVer)   { $nVer.GetAttribute('Value') }      else { $null }
    $longText = if ($nLong)  { $nLong.InnerText }                 else { $null }
    $logText  = if ($nLog)   { $nLog.InnerText }                  else { $null }
    $access   = if ($nAcc)   { $nAcc.GetAttribute('Value') }      else { $null }
    $thumb    = if ($nThumb) { $nThumb.GetAttribute('Value') }    else { $null }
    $shots    = @($root.SelectNodes('Screenshot') | ForEach-Object { $_.GetAttribute('Value') })

    return [pscustomobject]@{
        ModVersion      = $ver
        LongDescription = $longText
        ChangeLog       = $logText
        AccessLevel     = $access
        Thumbnail       = $thumb
        Screenshots     = $shots
    }
}

# 从磁盘读发布配置（纯只读，不写回）
function Read-PublishConfigSummary {
    param([Parameter(Mandatory = $true)][string]$Path)
    return ConvertFrom-PublishXmlText -Raw ([System.IO.File]::ReadAllText($Path))
}

function Resolve-ModRelativePath {
    param([string]$ModDir, [string]$Value)
    if ([string]::IsNullOrWhiteSpace($Value)) { return $null }
    return (Join-Path $ModDir ($Value -replace '/', '\'))
}

# ============================================================================
# 结果收集（供最后的总结使用）
# ============================================================================
$result = [ordered]@{
    Version        = $Version
    OldVersion     = '(未读取)'
    ChangeLogLen   = 0
    LongDescLen    = 0
    BuildResult    = '未执行'
    PublishResult  = '未执行'
    PageResult     = '未执行'
}

# DryRun = 只读模式：不写文件、不构建、不发布、不提交
$noWriteMode = [bool]$DryRun

Write-Head "TaxRateTweak 一键发布  ——  目标版本 $Version$(if ($DryRun) { '   [DryRun]' })"
Write-Kv '仓库根'   $RepoRoot
Write-Kv '模组目录' $ModDir
Write-Kv '发布目录' $StagingDir
Write-Kv '提交/tag' $(if ($Commit) { '是' } else { '否（默认，仅提示）' })
Write-Kv '推送远端' $(if (-not $Commit) { '(未提交，不涉及)' } elseif ($NoPush) { '否（-NoPush）' } else { "是（origin/$GitBranch + tags）" })
Write-Kv '发布'     $(if ($DryRun) { '否（DryRun）' } elseif ($SkipPublish) { '否（-SkipPublish）' } else { '是' })

if ($DryRun -and $Commit) {
    Write-Warn '-DryRun 与 -Commit 同时给出：DryRun 优先，不会提交。'
}
if ($NoPush -and -not $Commit) {
    Write-Warn '-NoPush 单独给出没有意义（未加 -Commit 就不会提交），已忽略。'
}

# ============================================================================
# 步骤 1/11 —— 参数与版本号校验
# ============================================================================
Write-Head '步骤 1/11  参数与版本号校验'

if ($Version -notmatch '^\d+\.\d+\.\d+$') {
    Stop-Failure "-Version '$Version' 格式非法，要求形如 1.8.29（^\d+\.\d+\.\d+$）。"
}
Write-Ok "-Version 格式合法：$Version"

if ([string]::IsNullOrWhiteSpace($ChangeLog)) {
    Stop-Failure '-ChangeLog 不能为空。'
}
if ($ChangeLog.Contains('`r`n')) {
    Stop-Failure '-ChangeLog 里出现了字面量 `r`n（反引号 r 反引号 n）。发布器不会把它当换行，页面上会留下满屏字面 rn；请直接使用真实换行。'
}
if ($ChangeLog -match '^\s*v?\d+\.\d+\.\d+\s*:') {
    Stop-Failure '-ChangeLog 不要自带 "v<版本>: " 前缀，脚本会自动加上；否则会重复。'
}
if ($ChangeLog -match '</ChangeLog>') {
    Stop-Failure '-ChangeLog 里不允许出现 </ChangeLog> 字样，会破坏 XML 结构。'
}
Write-Ok "-ChangeLog 校验通过（正文 $($ChangeLog.Length) 字符）"

if (-not (Test-Path -LiteralPath $ConfigPath)) {
    Stop-Failure "找不到发布配置：$ConfigPath"
}

$current = Read-PublishConfigSummary -Path $ConfigPath
$result.OldVersion = $current.ModVersion
if (-not $current.ModVersion) {
    Stop-Failure "无法从 $ConfigRel 读到 <ModVersion>。"
}
if ($current.ModVersion -notmatch '^\d+\.\d+\.\d+$') {
    Stop-Failure "当前 ModVersion '$($current.ModVersion)' 格式非法，无法比较大小。"
}
$curVerObj = [version]$current.ModVersion
$newVerObj = [version]$Version
if ($newVerObj -lt $curVerObj) {
    Stop-Failure "新版本号 $Version 小于当前 ModVersion $($current.ModVersion)，不允许回退发布。"
}
if ($newVerObj -eq $curVerObj) {
    Write-Warn "当前 ModVersion 已经是 $Version：按「上次未跑完、继续本次发布」处理，版本号保持不变。"
} else {
    Write-Ok "版本号递增检查通过：$($current.ModVersion)  ->  $Version"
}

# 工具链存在性（只针对后面真正会用到的、且非 DryRun 分支会用到的可执行文件）
$toolChecks = @(
    @{ Name = 'node';  Cmd = 'node' }
    @{ Name = 'dotnet'; Cmd = 'dotnet' }
    @{ Name = 'git';   Cmd = 'git' }
)
foreach ($t in $toolChecks) {
    if (-not (Get-Command $t.Cmd -ErrorAction SilentlyContinue)) {
        Stop-Failure "PATH 里找不到命令 '$($t.Cmd)'。"
    }
}
Write-Ok "node / dotnet / git 均可在 PATH 中找到"

# ============================================================================
# 步骤 2/11 —— 打印改动清单
# ============================================================================
Write-Head '步骤 2/11  将要做的改动清单'

$rawConfig = [System.IO.File]::ReadAllText($ConfigPath)
$mVerTag = [regex]::Match($rawConfig, '<ModVersion\s+Value="[^"]*"\s*/>')
if (-not $mVerTag.Success) {
    Stop-Failure "在 $ConfigRel 里没有匹配到 <ModVersion Value=\"...\" /> 标签。"
}
$verLine = Get-LineNumberAt -Text $rawConfig -Index $mVerTag.Index

Write-Host "  版本号需同步 5 处：" -ForegroundColor White
Write-Host ("   1) {0}:{1}  {2}  ->  <ModVersion Value=""{3}"" />" -f $ConfigRel, $verLine, $mVerTag.Value.Trim(), $Version)

$planIndex = 1
foreach ($rel in $SourceVersionFiles) {
    $abs = Join-Path $ModDir $rel
    if (-not (Test-Path -LiteralPath $abs)) { Stop-Failure "找不到源文件：$abs" }
    $txt = [System.IO.File]::ReadAllText($abs)
    foreach ($mm in [regex]::Matches($txt, '1\.8\.\d+')) {
        $planIndex++
        $ln = Get-LineNumberAt -Text $txt -Index $mm.Index
        Write-Host ("   {0}) {1}:{2}  '{3}'  ->  '{4}'" -f $planIndex, $rel, $ln, $mm.Value, $Version)
    }
}
if ($planIndex -ne 5) {
    Stop-Failure "版本号位置数量异常：XML 1 处 + 源文件 $($planIndex - 1) 处，共 $planIndex 处，预期 5 处。"
}
Write-Ok '5 处版本号位置齐全（XML 1 + .cs 2 + .mjs 2）'

Write-Host ''
Write-Host "  ChangeLog：" -ForegroundColor White
Write-Kv '   当前内容长度' "$($current.ChangeLog.Length) 字符（含历史拼接，将被整体替换）"
Write-Kv '   新内容长度'   "$((("v${Version}: $ChangeLog")).Length) 字符（单条，无历史拼接）"
Write-Kv '   新内容预览'   ("v${Version}: $ChangeLog")

if ($noWriteMode) {
    Write-Warn '-DryRun：以上改动只做模拟，不落盘。'
}

# ============================================================================
# 步骤 3/11 —— 写版本号（5 处）+ 逐处回读校验
# ============================================================================
Write-Head '步骤 3/11  写入版本号并回读校验'

# --- 3.1 XML 的 <ModVersion /> ---
$newTag    = '<ModVersion Value="' + $Version + '" />'
$newXmlRaw = $rawConfig.Remove($mVerTag.Index, $mVerTag.Length).Insert($mVerTag.Index, $newTag)
if ($noWriteMode) {
    Write-Sub "-DryRun：不写 $ConfigRel，仅在内存里模拟改写"
} else {
    Write-TextFileNoBom -Path $ConfigPath -Text $newXmlRaw
    Write-Sub "已写入 $ConfigRel"
}

# --- 3.2 两个 .cs 与一个 .mjs 里的 1.8.x ---
$simulatedSources = @{}
$replacedTotal = 0
foreach ($rel in $SourceVersionFiles) {
    $abs = Join-Path $ModDir $rel
    $txt = [System.IO.File]::ReadAllText($abs)
    $hits = [regex]::Matches($txt, '1\.8\.\d+').Count
    $newTxt = [regex]::Replace($txt, '1\.8\.\d+', (ConvertTo-RegexReplacement $Version))
    $simulatedSources[$rel] = $newTxt
    $replacedTotal += $hits
    if ($noWriteMode) {
        Write-Sub "-DryRun：不写 $rel，仅在内存里模拟替换 $hits 处"
    } else {
        Write-TextFileNoBom -Path $abs -Text $newTxt
        Write-Sub "已写入 $rel（替换 $hits 处）"
    }
}
if (-not $noWriteMode) {
    Write-Ok "版本号写入完成，共 5 处（XML 1 + 源文件 $replacedTotal）"
}

# --- 3.3 逐处回读校验 ---
# 真实运行：从磁盘回读写入结果（证明确实落盘）。
# DryRun   ：校验内存里的模拟结果（证明 5 处都能被正则命中并替换成新值）。
$verifyFailures = New-Object System.Collections.Generic.List[string]

$checkRawConfig = if ($noWriteMode) { $newXmlRaw } else { [System.IO.File]::ReadAllText($ConfigPath) }
$mVerAfter = [regex]::Match($checkRawConfig, '<ModVersion\s+Value="([^"]*)"\s*/>')
if (-not $mVerAfter.Success -or $mVerAfter.Groups[1].Value -ne $Version) {
    $verifyFailures.Add("$ConfigRel 的 <ModVersion> 回读为 '$($mVerAfter.Groups[1].Value)'，期望 '$Version'")
} else {
    Write-Ok "回读 1/5  $ConfigRel  <ModVersion Value=`"$Version`" />"
}

$probeIndex = 1
foreach ($p in $VersionProbes) {
    $probeIndex++
    $abs = Join-Path $ModDir $p.Rel
    $txt = if ($noWriteMode) { $simulatedSources[$p.Rel] } else { [System.IO.File]::ReadAllText($abs) }
    $expect = ($p.Probe -f $Version)
    if ($txt.Contains($expect)) {
        Write-Ok "回读 $probeIndex/5  $($p.Desc)  ->  $expect"
    } else {
        $verifyFailures.Add("$($p.Rel) 中找不到期望串：$expect")
    }
    if (-not $noWriteMode) {
        # 关键：必须排除"恰好等于目标版本号"的匹配。用 '1\.8\.\d+' 笼统计数会把刚写进去的新版本号
        # 也当成残留（1.8.29 就能被该正则匹配），从而把一次成功的写入判成失败。
        $left = @([regex]::Matches($txt, '1\.8\.\d+') | Where-Object { $_.Value -ne $Version }).Count
        if ($left -gt 0) { $verifyFailures.Add("$($p.Rel) 里仍残留 $left 处非目标版本号") }
    }
}
if ($verifyFailures.Count -gt 0) {
    foreach ($f in $verifyFailures) { Write-Err $f }
    Stop-Failure '版本号回读校验失败，5 处未全部改到新值。'
}
Write-Ok '回读校验通过：5 处全部为新值'

# ============================================================================
# 步骤 4/11 —— 重写 ChangeLog（单条，去掉历史拼接）
# ============================================================================
Write-Head '步骤 4/11  重写 ChangeLog'

$newChangeLogText = "v${Version}: $ChangeLog"
Write-Kv '新 ChangeLog' $newChangeLogText
Write-Kv '长度'         "$($newChangeLogText.Length) 字符"

$logMatches = [regex]::Matches($checkRawConfig, '<ChangeLog>.*?</ChangeLog>', 'Singleline')
if ($logMatches.Count -ne 1) {
    Stop-Failure "在 $ConfigRel 里匹配到 $($logMatches.Count) 个 <ChangeLog> 元素，预期恰好 1 个。"
}

$escaped   = ConvertTo-XmlEscaped $newChangeLogText
$newLogTag = '<ChangeLog>' + $escaped + '</ChangeLog>'
$m         = $logMatches[0]

if ($noWriteMode) {
    Write-Sub '-DryRun：不写回配置，仅在内存里把旧 ChangeLog（含历史拼接）整体替换为上面这一条。'
    $finalXmlRaw = $checkRawConfig.Remove($m.Index, $m.Length).Insert($m.Index, $newLogTag)
} else {
    $rawNow      = [System.IO.File]::ReadAllText($ConfigPath)
    $finalXmlRaw = $rawNow.Remove($m.Index, $m.Length).Insert($m.Index, $newLogTag)
    Write-TextFileNoBom -Path $ConfigPath -Text $finalXmlRaw
}

# 回读：解析成 XML 后比较逻辑内容（实体转义会被自动还原）
$afterSummary = if ($noWriteMode) { ConvertFrom-PublishXmlText -Raw $finalXmlRaw } else { Read-PublishConfigSummary -Path $ConfigPath }
if ($afterSummary.ChangeLog -ne $newChangeLogText) {
    Stop-Failure "ChangeLog 回读不一致：`n  期望: $newChangeLogText`n  实际: $($afterSummary.ChangeLog)"
}
if ([regex]::Matches($afterSummary.ChangeLog, 'v\d+\.\d+\.\d+').Count -gt 1) {
    Stop-Failure 'ChangeLog 回读里仍有多个版本标记，历史拼接没清干净。'
}
if ($afterSummary.ChangeLog.Contains('`r`n')) {
    Stop-Failure 'ChangeLog 回读里出现字面量 `r`n，站点上会渲染成字面 rn。'
}
if ($noWriteMode) {
    Write-Ok 'ChangeLog 模拟结果自检一致：单条、无历史拼接（未写盘）'
} else {
    Write-Ok 'ChangeLog 写入并回读一致：单条、无历史拼接'
}

# ============================================================================
# 步骤 5/11 —— 平台闸门检查
# ============================================================================
Write-Head '步骤 5/11  平台闸门检查（发布器会拒收超限内容）'

# 闸门基于"最终会提交给发布器的那份内容"：
# 真实运行 = 磁盘上的配置（顺带证明它就是一份合法 XML）；DryRun = 内存里的模拟结果。
if ($noWriteMode) {
    $gate = ConvertFrom-PublishXmlText -Raw $finalXmlRaw
    Write-Sub '-DryRun：闸门基于内存模拟出来的配置（长描述、图片、AccessLevel 取自磁盘，未改动）。'
} else {
    $gate = Read-PublishConfigSummary -Path $ConfigPath
}

$gateFailures = New-Object System.Collections.Generic.List[string]

# --- 5.1 LongDescription ---
$ldLen = $gate.LongDescription.Length
$ldMargin = $MaxLongDescriptionLen - $ldLen
Write-Kv '5.1 LongDescription' "$ldLen 字符 / 上限 $MaxLongDescriptionLen（余量 $ldMargin）$(if ($ldMargin -lt 500) { '  <-- 余量偏小' })"
if ($ldLen -lt 1 -or $ldLen -gt $MaxLongDescriptionLen) {
    $gateFailures.Add("LongDescription 长度 $ldLen 越界（必须 1..$MaxLongDescriptionLen）")
} else {
    Write-Ok "LongDescription 长度合规（余量 $ldMargin 字符）"
}

# --- 5.2 ChangeLog ---
$clLen = $gate.ChangeLog.Length
$clMargin = $MaxChangeLogLen - $clLen
Write-Kv '5.2 ChangeLog' "$clLen 字符 / 上限 $MaxChangeLogLen（余量 $clMargin）"
if ($clLen -lt 1 -or $clLen -gt $MaxChangeLogLen) {
    $gateFailures.Add("ChangeLog 长度 $clLen 越界（必须 1..$MaxChangeLogLen）")
} else {
    Write-Ok "ChangeLog 长度合规（余量 $clMargin 字符）"
}

# --- 5.3 截图 / 缩略图文件 ---
Write-Host ''
Write-Host '  5.3 图片资源：' -ForegroundColor White
$imageEntries = @()
foreach ($s in $gate.Screenshots)  { $imageEntries += [pscustomobject]@{ Kind = 'Screenshot'; Value = $s } }
if ($gate.Thumbnail) { $imageEntries += [pscustomobject]@{ Kind = 'Thumbnail';  Value = $gate.Thumbnail } }

if ($imageEntries.Count -eq 0) {
    $gateFailures.Add('配置里没有任何 <Screenshot> / <Thumbnail> 元素')
}

foreach ($e in $imageEntries) {
    $abs = Resolve-ModRelativePath -ModDir $ModDir -Value $e.Value
    if (-not $abs -or -not (Test-Path -LiteralPath $abs)) {
        $gateFailures.Add("$($e.Kind) 文件不存在：$($e.Value)")
        Write-Err "$($e.Kind) 不存在：$($e.Value)"
        continue
    }
    $fi = Get-Item -LiteralPath $abs
    $dim = Get-PngDimensions -Path $abs
    $dimText = if ($dim) { "$($dim.Width)x$($dim.Height)" } else { '(非 PNG 或无法读取尺寸)' }
    $overLimit = $fi.Length -gt $MaxScreenshotBytes
    $flag = if ($overLimit) { '  <-- 超过 2.1MB' } else { '' }
    Write-Host ("      {0,-11} {1,-32} {2,12}  {3}{4}" -f $e.Kind, $e.Value, $dimText, (Format-FileSize $fi.Length), $flag)

    if ($e.Kind -eq 'Screenshot' -and $overLimit) {
        $gateFailures.Add("截图 $($e.Value) 为 $(Format-FileSize $fi.Length)，超过 2.1MB 上限")
    }
    if ($e.Kind -eq 'Thumbnail' -and $overLimit) {
        Write-Warn "缩略图 $($e.Value) 超过 2.1MB（$(Format-FileSize $fi.Length)）。平台对 Thumbnail 的限制未在本次需求中明确，仅警告不判失败。"
    }
    if ($dim -and ($dim.Width -lt 16 -or $dim.Height -lt 16)) {
        Write-Warn "$($e.Value) 尺寸异常偏小：$dimText"
    }
}
if ($gate.Screenshots.Count -gt 0) { Write-Ok "截图合计 $($gate.Screenshots.Count) 张，全部存在" }

# --- 5.4 AccessLevel ---
Write-Host ''
$accessVal = if ($gate.AccessLevel) { $gate.AccessLevel } else { '(空)' }
Write-Kv '5.4 AccessLevel' $accessVal
if ($accessVal -eq 'Public') {
    Write-Ok 'AccessLevel = Public'
} else {
    Write-Warn "AccessLevel = '$accessVal'，期望 'Public'。若是有意为之请忽略；否则发布后商店页可见性会不符预期。"
}

if ($gateFailures.Count -gt 0) {
    Write-Host ''
    foreach ($f in $gateFailures) { Write-Err $f }
    Stop-Failure '闸门检查未通过，已中止（不会构建、不会发布）。'
}
Write-Host ''
Write-Ok '闸门检查全部通过'

$result.LongDescLen  = $ldLen
$result.ChangeLogLen = $clLen

# ============================================================================
# 步骤 6/11 —— node --check + restore + build（0 警告 0 错误）
# ============================================================================
Write-Head '步骤 6/11  构建校验'

$nodeRel = 'UI\TaxRateTweak.mjs'

# 6.1 node --check（只读，DryRun 也执行）
Write-Sub "node --check $nodeRel"
$nodeRes = Invoke-NativeCapture -FilePath 'node' -Arguments @('--check', ($nodeRel -replace '\\', '/')) -WorkingDirectory $ModDir
if ($nodeRes.Lines.Count -gt 0) { $nodeRes.Lines | ForEach-Object { Write-Host "      $_" -ForegroundColor DarkGray } }
if ($nodeRes.ExitCode -ne 0) {
    $result.BuildResult = '失败（node --check）'
    Stop-Failure "node --check 失败（exit=$($nodeRes.ExitCode)）。"
}
Write-Ok 'node --check 通过'

$restoreCmd = 'dotnet restore TaxRateTweak.csproj --locked-mode -v minimal'
$buildCmd   = 'dotnet build TaxRateTweak.csproj -c Release --no-restore -v minimal'

if ($noWriteMode) {
    Write-Sub '-DryRun：跳过 restore/build（会写入 obj/、bin/ 及 pdx-staging，违反 DryRun 不写文件约束）。'
    Write-Sub "真实运行时会依次执行："
    Write-Host "      $restoreCmd" -ForegroundColor DarkGray
    Write-Host "      $buildCmd" -ForegroundColor DarkGray
    $result.BuildResult = "跳过（DryRun）；node --check 通过"
} else {
    # 6.2 restore
    Write-Sub "dotnet restore（--locked-mode）"
    $restoreRes = Invoke-NativeCapture -FilePath 'dotnet' -Arguments @('restore', 'TaxRateTweak.csproj', '--locked-mode', '-v', 'minimal') -WorkingDirectory $ModDir
    $restoreRes.Lines | ForEach-Object { Write-Host "      $_" -ForegroundColor DarkGray }
    if ($restoreRes.ExitCode -ne 0) {
        $result.BuildResult = '失败（restore）'
        Stop-Failure "dotnet restore 失败（exit=$($restoreRes.ExitCode)）。"
    }
    $restoreProbs = Get-BuildProblemLines -Lines $restoreRes.Lines
    if ($restoreProbs.Count -gt 0) {
        $result.BuildResult = '失败（restore 有警告/错误）'
        Stop-Failure "dotnet restore 输出里出现警告/错误字样：`n$($restoreProbs -join "`n")"
    }
    Write-Ok 'dotnet restore 通过（0 警告 0 错误）'

    # 6.3 build（含 Burst 三平台编译）
    Write-Sub 'dotnet build -c Release（含 Burst 三平台编译，耗时较长）'
    $buildRes = Invoke-NativeCapture -FilePath 'dotnet' -Arguments @('build', 'TaxRateTweak.csproj', '-c', 'Release', '--no-restore', '-v', 'minimal') -WorkingDirectory $ModDir
    $buildRes.Lines | ForEach-Object { Write-Host "      $_" -ForegroundColor DarkGray }
    if ($buildRes.ExitCode -ne 0) {
        $result.BuildResult = '失败（build）'
        Stop-Failure "dotnet build 失败（exit=$($buildRes.ExitCode)）。"
    }
    $buildProbs = Get-BuildProblemLines -Lines $buildRes.Lines
    if ($buildProbs.Count -gt 0) {
        $result.BuildResult = '失败（build 有警告/错误）'
        Stop-Failure "dotnet build 输出里出现警告/错误字样（要求 0 警告 0 错误）：`n$($buildProbs -join "`n")"
    }
    Write-Ok 'dotnet build 通过（0 警告 0 错误）'
    $result.BuildResult = '成功（0 警告 0 错误）'
}

# ============================================================================
# 步骤 7/11 —— 发布目录必需文件校验
# ============================================================================
Write-Head '步骤 7/11  发布目录必需文件校验'

if (-not (Test-Path -LiteralPath $StagingDir)) {
    if ($noWriteMode) {
        Write-Warn "发布目录不存在：$StagingDir（DryRun 未构建，属预期）"
    } else {
        Stop-Failure "发布目录不存在：$StagingDir"
    }
}

$missingStaged = New-Object System.Collections.Generic.List[string]
foreach ($f in $RequiredStagedFiles) {
    $abs = Join-Path $StagingDir $f
    if (Test-Path -LiteralPath $abs) {
        $fi = Get-Item -LiteralPath $abs
        Write-Host ("      [OK]   {0,-32} {1,12}   {2}" -f $f, (Format-FileSize $fi.Length), $fi.LastWriteTime.ToString('yyyy-MM-dd HH:mm:ss'))
    } else {
        $missingStaged.Add($f)
        Write-Host ("      [缺失] {0}" -f $f) -ForegroundColor Red
    }
}
if ($missingStaged.Count -gt 0) {
    if ($noWriteMode) {
        Write-Warn "-DryRun：$($missingStaged.Count) 个必需文件缺失（未构建，属预期；真实运行会在构建后校验并判失败）：$($missingStaged -join ', ')"
    } else {
        Stop-Failure "发布目录缺少必需文件：$($missingStaged -join ', ')"
    }
} else {
    Write-Ok "发布目录 7 个必需文件齐全：$($RequiredStagedFiles -join ', ')"
}

# ============================================================================
# 步骤 8/11 —— git 提交与打 tag
# ============================================================================
Write-Head '步骤 8/11  git 提交与打 tag'

$firstSentence = Get-FirstSentence $ChangeLog
$commitMsg = "TaxRateTweak ${Version}：$firstSentence"
$tagName   = "v$Version"

if (-not $Commit) {
    Write-Warn '未加 -Commit：跳过提交与打 tag。真实发布时建议手动执行或加 -Commit。'
    Write-Sub  "将执行的提交信息：$commitMsg"
    Write-Sub  "将创建的 tag：$tagName"
} elseif ($noWriteMode) {
    Write-Warn '-DryRun：跳过 git commit / tag（DryRun 不做任何写入）。'
    Write-Sub  "将执行的提交信息：$commitMsg"
    Write-Sub  "将创建的 tag：$tagName"
} else {
    $addRes = Invoke-NativeCapture -FilePath 'git' -Arguments @('-C', $RepoRoot, 'add', '-A')
    if ($addRes.ExitCode -ne 0) { Stop-Failure "git add -A 失败：$($addRes.Text)" }
    Write-Ok 'git add -A'

    $commitRes = Invoke-NativeCapture -FilePath 'git' -Arguments @('-C', $RepoRoot, 'commit', '-m', $commitMsg)
    $commitRes.Lines | ForEach-Object { Write-Host "      $_" -ForegroundColor DarkGray }
    if ($commitRes.ExitCode -ne 0) { Stop-Failure "git commit 失败（exit=$($commitRes.ExitCode)）：$($commitRes.Text)" }
    Write-Ok "git commit：$commitMsg"

    $tagRes = Invoke-NativeCapture -FilePath 'git' -Arguments @('-C', $RepoRoot, 'tag', '-a', $tagName, '-m', $commitMsg)
    if ($tagRes.ExitCode -ne 0) { Stop-Failure "git tag -a $tagName 失败（exit=$($tagRes.ExitCode)）：$($tagRes.Text)" }
    Write-Ok "git tag -a $tagName"

    if ($NoPush) {
        Write-Warn '-NoPush：跳过推送。'
    } else {
        foreach ($pushArgs in @(@('push', 'origin', $GitBranch), @('push', 'origin', '--tags'))) {
            $label = "git $($pushArgs -join ' ')"
            $attempt = 0
            $ok = $false
            while (-not $ok -and $attempt -lt ($GitMaxRetries + 1)) {
                $attempt++
                Write-Sub "$label  第 $attempt 次尝试"
                $pr = Invoke-NativeCapture -FilePath 'git' -Arguments (@('-C', $RepoRoot) + $pushArgs)
                if ($pr.ExitCode -eq 0) {
                    $ok = $true
                    Write-Ok "$label 成功"
                } else {
                    Write-Warn "$label 失败（exit=$($pr.ExitCode)）：$($pr.Text)"
                    if ($attempt -lt ($GitMaxRetries + 1)) { Start-Sleep -Seconds $RetryDelaySeconds }
                }
            }
            if (-not $ok) {
                Stop-Failure "$label 连续 $($GitMaxRetries + 1) 次失败（SSL/443 之类网络问题请检查代理或稍后重试）。"
            }
        }
    }
}

# ============================================================================
# 步骤 9/11 —— 发布
# ============================================================================
Write-Head '步骤 9/11  调用 ModPublisher 发布'

$publishSkippedReason = $null
if ($DryRun)          { $publishSkippedReason = '跳过（-DryRun）' }
elseif ($SkipPublish) { $publishSkippedReason = '跳过（-SkipPublish）' }

if ($publishSkippedReason) {
    Write-Warn $publishSkippedReason
    if (-not $DryRun) {
        Write-Sub '将执行的命令（工作目录 = 模组源码目录）：'
        Write-Host "      ModPublisher.exe NewVersion `"$ConfigRel`" -c `"$StagingDirCmd`"" -ForegroundColor DarkGray
    }
    $result.PublishResult = $publishSkippedReason
    $result.PageResult    = $publishSkippedReason
} else {
    if (-not (Test-Path -LiteralPath $PublisherExe)) {
        Stop-Failure "找不到发布器：$PublisherExe"
    }

    $pubAttempt = 0
    $pubOk = $false
    $pubLast = $null
    $pubLines = @()

    while (-not $pubOk -and $pubAttempt -lt ($PublishMaxRetries + 1)) {
        $pubAttempt++
        Write-Sub "第 $pubAttempt / $($PublishMaxRetries + 1) 次调用 ModPublisher"
        $pubLast = Invoke-NativeCapture -FilePath $PublisherExe `
            -Arguments @('NewVersion', ($ConfigRel -replace '\\', '/'), '-c', $StagingDirCmd) `
            -WorkingDirectory $ModDir
        $pubLines = $pubLast.Lines
        $pubLines | ForEach-Object { Write-Host "      $_" -ForegroundColor DarkGray }

        if ($pubLast.Text -match [regex]::Escape($PublisherOkMarker)) {
            $pubOk = $true
            Write-Ok "发布器报告成功（第 $pubAttempt 次）：$PublisherOkMarker"
            break
        }

        Write-Warn "未出现成功标志（exit=$($pubLast.ExitCode)）"
        if ($pubLast.Text -match '(?i)ssl|certificate|443|unauthor|not logged|login') {
            Write-Warn '输出含 SSL / 证书 / 未登录 之类字样，按网络问题重试。'
        }
        if ($pubAttempt -lt ($PublishMaxRetries + 1)) { Start-Sleep -Seconds $RetryDelaySeconds }
    }

    if (-not $pubOk) {
        $result.PublishResult = "失败（$pubAttempt 次尝试均未成功）"
        $failLine = $pubLines | Where-Object { $_.TrimStart().StartsWith($PublisherFailPrefix) } | Select-Object -First 1
        Write-Host ''
        Write-Host '  ---- 发布器输出（原样回显失败行）----' -ForegroundColor Red
        if ($failLine) { Write-Host "  $failLine" -ForegroundColor Red }
        else { $pubLines | ForEach-Object { Write-Host "  $_" -ForegroundColor Red } }
        Stop-Failure "发布器在 $pubAttempt 次尝试后仍未成功（未出现 '$PublisherOkMarker'）。"
    }
    $result.PublishResult = "成功（第 $pubAttempt 次尝试）"
}

# ============================================================================
# 步骤 10/11 —— 页面回读（无头 Chrome）
# ============================================================================
Write-Head '步骤 10/11  线上页面版本号回读'

if ($publishSkippedReason) {
    Write-Warn "未发布，跳过页面回读（$publishSkippedReason）。"
} else {
    if (-not (Test-Path -LiteralPath $ChromeExe)) {
        Stop-Failure "找不到 Chrome：$ChromeExe"
    }

    $profileDir = Join-Path $env:TEMP ("pdx-pagecheck-" + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $profileDir -Force | Out-Null
    Write-Sub "临时 user-data-dir：$profileDir"
    Write-Sub "站点缓存延迟通常 10-15 分钟，最多尝试 $($PageCheckMaxRetries + 1) 次、每次间隔 $PageCheckDelaySec 秒。"

    $pageOk = $false
    $pageAttempt = 0
    $pageFailures = 0

    try {
        while (-not $pageOk -and $pageAttempt -lt ($PageCheckMaxRetries + 1)) {
            if ($pageAttempt -gt 0) { Start-Sleep -Seconds $PageCheckDelaySec }
            $pageAttempt++
            $cacheBuster = [guid]::NewGuid().ToString('N')
            $url = "$StoreUrl" + "?cb=$cacheBuster&_=$([DateTimeOffset]::UtcNow.ToUnixTimeSeconds())"
            Write-Sub "第 $pageAttempt / $($PageCheckMaxRetries + 1) 次渲染：$url"

            $chromeArgs = @(
                '--headless=new'
                '--disable-gpu'
                '--no-first-run'
                '--no-default-browser-check'
                '--disable-extensions'
                '--virtual-time-budget=25000'
                "--user-data-dir=$profileDir"
                '--dump-dom'
                $url
            )
            $domRes = Invoke-NativeCapture -FilePath $ChromeExe -Arguments $chromeArgs
            $domText = $domRes.Text

            if ([string]::IsNullOrWhiteSpace($domText)) {
                $pageFailures++
                Write-Warn "本次未取到 DOM（exit=$($domRes.ExitCode)），视为未命中。"
            } elseif ($domText.Contains($Version)) {
                $pageOk = $true
                Write-Ok "页面已出现新版本号 $Version（第 $pageAttempt 次尝试）"
                $idx = $domText.IndexOf($Version)
                $snippet = $domText.Substring([Math]::Max(0, $idx - 80), [Math]::Min(200, $domText.Length - [Math]::Max(0, $idx - 80))).Replace("`n", ' ').Replace("`r", '')
                Write-Sub "命中上下文：…$snippet…"
            } else {
                $pageFailures++
                Write-Warn "DOM 里还没出现 $Version（第 $pageAttempt 次尝试，DOM $($domText.Length) 字符）"
            }
        }
    }
    finally {
        if (Test-Path -LiteralPath $profileDir) {
            Remove-Item -LiteralPath $profileDir -Recurse -Force -ErrorAction SilentlyContinue
            Write-Sub "已清理临时 user-data-dir"
        }
    }

    if ($pageOk) {
        $result.PageResult = "成功（第 $pageAttempt 次尝试命中 $Version）"
    } else {
        $result.PageResult = "未刷新（$pageAttempt 次尝试均未见 $Version）"
        Write-Host ''
        Write-Host '  发布器成功但页面尚未刷新，请稍后人工确认。' -ForegroundColor Yellow
        Write-Host "  建议：10-15 分钟后打开 $StoreUrl 手工确认版本号是否已变为 $Version。" -ForegroundColor Yellow
        Write-Host ''
        exit 3
    }
}

# ============================================================================
# 步骤 11/11 —— 总结
# ============================================================================
Write-Head '步骤 11/11  总结'

Write-Kv '版本'         "$Version（原 $($result.OldVersion)）"
Write-Kv '变更日志字数' "$($result.ChangeLogLen) / $MaxChangeLogLen（余量 $($MaxChangeLogLen - $result.ChangeLogLen)）"
Write-Kv '描述字数'     "$($result.LongDescLen) / $MaxLongDescriptionLen（余量 $($MaxLongDescriptionLen - $result.LongDescLen)）"
Write-Kv '构建结果'     $result.BuildResult
Write-Kv '提交/tag'     $(if ($Commit -and -not $DryRun) { '已执行' } else { '未执行' })
Write-Kv '推送'         $(if ($Commit -and -not $DryRun -and -not $NoPush) { '已推送' } else { '未推送' })
Write-Kv '发布结果'     $result.PublishResult
Write-Kv '页面回读'     $result.PageResult
Write-Host ''

if ($DryRun) {
    Write-Host '  DryRun 完成：未写入任何文件、未构建、未发布。' -ForegroundColor Cyan
} else {
    Write-Host '  全部步骤完成。' -ForegroundColor Green
}
Write-Host ''

exit 0
