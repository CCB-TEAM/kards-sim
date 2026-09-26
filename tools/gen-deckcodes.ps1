<#
.SYNOPSIS
    从私服参考实现的数据表生成 KardsSim 的卡组码表 deckcodes.json。

.DESCRIPTION
    KARDS 的卡组码里每张牌用 2 个字符表示（见 README「卡组码」一节），
    这 2 个字符是客户端数据表 deckCodeIDsTable2.json 里的 deck_code_id，
    它不在卡牌蓝图（.uasset）里 —— 从 _input/Cards 抽出来的 cards.json
    没有这个字段。所以这张表只能来自客户端数据表本身，本仓库把它固化下来，
    运行时不再依赖外部路径。

    来源：fyserver/private-server 参考实现自带的
          library/deckCodeIDsTable2.json（2500+ 行，字段 ID / card / deck_code_id）。

    两个必须注意的点：
      1. deck_code_id 是**大小写敏感**的：'0A' 和 '0a' 是两张不同的卡
         （card_event_the_empire vs card_event_daylight_bombing）。
         用大小写不敏感的字典会把它们搞混。
      2. 同一份表里**真的有 17 个代码被两张卡共用**（不是大小写差异，是完全相同的字符串）。
         形态很一致：一张 Anzac（OceaniaStorm 新阵营）卡，对一张别的阵营的 _bal（平衡版）卡，
         例如 xX = card_unit_dingo_armored_car(Anzac) 与 card_unit_lancaster_bal(Britain)。
         这类代码写成数组，解析时按「卡库里有哪张 + 阵营合法性」挑（见 Core/DeckCode.cs）。
      3. 码表里有些卡本模拟器的卡库没有（导出不全 / 新版本新增 / _bal 平衡变体），
         这里照样写进去 —— 解析器要靠它把「未知代码」和「本卡库没这张牌」区分开。

.EXAMPLE
    pwsh tools/gen-deckcodes.ps1
    pwsh tools/gen-deckcodes.ps1 -Table H:\fyserver\fyserver\library\deckCodeIDsTable2.json
#>
[CmdletBinding()]
param(
    [string]$Table = 'H:\fyserver\fyserver\library\deckCodeIDsTable2.json',
    [string]$Out = (Join-Path (Split-Path -Parent $PSScriptRoot) 'deckcodes.json')
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path $Table)) { throw "找不到卡组码表: $Table" }

$rows = Get-Content $Table -Raw | ConvertFrom-Json

# 大小写敏感的累积：同一个 deck_code_id 出现两次是数据表事故，要报出来
$map = [System.Collections.Generic.Dictionary[string, System.Collections.Generic.List[string]]]::new([System.StringComparer]::Ordinal)
$dups = 0
foreach ($r in $rows) {
    $code = [string]$r.deck_code_id
    $card = [string]$r.card
    if ([string]::IsNullOrEmpty($code)) { continue }
    if (-not $map.ContainsKey($code)) {
        $map[$code] = [System.Collections.Generic.List[string]]::new()
    } elseif (-not $map[$code].Contains($card)) {
        $dups++
        Write-Warning "码表冲突: $code => $($map[$code] -join ' / ') / $card（都保留，解析时按卡库与阵营挑）"
    }
    if (-not $map[$code].Contains($card)) { [void]$map[$code].Add($card) }
}

$codes = $map.Keys | Sort-Object
$sb = [System.Text.StringBuilder]::new()
[void]$sb.AppendLine('{')
[void]$sb.AppendLine('  "_note": "KARDS 卡组码里 2 字符卡牌代码 -> 客户端卡牌资产名（asset/cards.json 的 id）。代码大小写敏感：0A 与 0a 是两张不同的卡。值可能是数组：同一代码被两张卡共用（Anzac 新卡 vs 别的阵营的 _bal 平衡版），解析时按「本卡库有哪张 + 阵营是否合法」挑。表里包含本模拟器卡库里没有的卡（导出不全/新版本），解析器据此区分「未知代码」与「本卡库没有这张牌」。",')
[void]$sb.AppendLine('  "_source": "fyserver 参考实现 library/deckCodeIDsTable2.json（客户端数据表导出）",')
[void]$sb.AppendLine('  "_generated_by": "tools/gen-deckcodes.ps1",')
[void]$sb.AppendLine('  "codes": {')
for ($i = 0; $i -lt $codes.Count; $i++) {
    $code = $codes[$i]
    $comma = if ($i -lt $codes.Count - 1) { ',' } else { '' }
    $list = $map[$code]
    if ($list.Count -eq 1) {
        [void]$sb.AppendLine(('    "{0}": "{1}"{2}' -f $code, $list[0], $comma))
    } else {
        $items = ($list | ForEach-Object { '"' + $_ + '"' }) -join ', '
        [void]$sb.AppendLine(('    "{0}": [{1}]{2}' -f $code, $items, $comma))
    }
}
[void]$sb.AppendLine('  }')
[void]$sb.AppendLine('}')

[System.IO.File]::WriteAllText($Out, $sb.ToString(), [System.Text.UTF8Encoding]::new($false))
Write-Output ("写入 {0}：{1} 条码表（来源 {2}，多候选冲突 {3}）" -f $Out, $map.Count, $Table, $dups)
