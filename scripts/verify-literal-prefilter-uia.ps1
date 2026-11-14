param([Parameter(Mandatory)][string]$Executable,[string]$RuntimeRoot)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$workspace=(Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$exe=(Resolve-Path -LiteralPath $Executable).Path
if(-not $exe.StartsWith($workspace+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'Expected workspace executable.'}
$dll=Join-Path (Split-Path $exe) 'DocSearch.dll'
if(-not(Test-Path -LiteralPath $dll -PathType Leaf)){throw 'DocSearch.dll missing.'}
$runtimeOverride=$null
if($RuntimeRoot){$runtimeOverride=(Resolve-Path -LiteralPath $RuntimeRoot).Path;if(-not(Test-Path -LiteralPath (Join-Path $runtimeOverride 'dotnet.exe') -PathType Leaf)){throw 'RuntimeRoot must contain dotnet.exe.'}}
$owned=Join-Path $workspace ('artifacts/literal-prefilter-uia-'+[guid]::NewGuid().ToString('N'))
$corpus=Join-Path $owned 'corpus';$mixed=Join-Path $corpus 'mixed';$none=Join-Path $corpus 'no-candidate'
New-Item -ItemType Directory -Path $owned,$mixed,$none | Out-Null
$padding='z'*300
$documents=[ordered]@{
 'none.txt'=('z'*1200)
 'sparse.txt'=($padding+'aba'+$padding+'西瓜'+$padding)
 'overlap.txt'=($padding+'ababa'+$padding)
 'unicode.txt'=($padding+'西瓜 ΩMEGA'+$padding)
 'all.txt'=($padding+'aba bab 西瓜 Ωmega'+$padding)
 'short.txt'='aba 西瓜'
 'extras.txt'=($padding+'🧭 ÉCOLE 東京 needleZ'+$padding)
}
foreach($item in $documents.GetEnumerator()){[IO.File]::WriteAllText((Join-Path $mixed $item.Key),$item.Value,[Text.UTF8Encoding]::new($false))}
[IO.File]::WriteAllText((Join-Path $none 'none-only.txt'),('z'*1200),[Text.UTF8Encoding]::new($false))
function Source-State {@(Get-ChildItem -LiteralPath $corpus -File -Recurse | Sort-Object FullName | ForEach-Object {[ordered]@{path=[IO.Path]::GetRelativePath($corpus,$_.FullName);bytes=$_.Length;mtimeUtcTicks=$_.LastWriteTimeUtc.Ticks;sha256=(Get-FileHash -LiteralPath $_.FullName).Hash}})}
$before=@(Source-State);$beforeJson=ConvertTo-Json -InputObject $before -Depth 6 -Compress
$uiaRoot='C:\Program Files\dotnet\shared\Microsoft.WindowsDesktop.App\6.0.11'
foreach($assembly in @('UIAutomationTypes.dll','UIAutomationClient.dll')){Add-Type -Path (Join-Path $uiaRoot $assembly)}
$desktop=[Windows.Automation.AutomationElement]::RootElement;$desc=[Windows.Automation.TreeScope]::Descendants
$owner=$null;$main=$null;$script:editor=$null;$cases=[Collections.Generic.List[object]]::new()
function Wait-Until([scriptblock]$Probe,[string]$Label,[int]$Seconds=30){$clock=[Diagnostics.Stopwatch]::StartNew();while($clock.Elapsed.TotalSeconds -lt $Seconds){if($null -ne $owner -and $owner.HasExited){throw ('Owned process exited: '+$Label)};$value=& $Probe;if($value){return $value};Start-Sleep -Milliseconds 50};throw ('UI condition timed out: '+$Label)}
function By-Id($root,[string]$id){$root.FindFirst($desc,[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::AutomationIdProperty,$id))}
function By-Name($root,[string]$name){$root.FindFirst($desc,[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::NameProperty,$name))}
function Owned-Windows {@($desktop.FindAll([Windows.Automation.TreeScope]::Children,[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ProcessIdProperty,$owner.Id)))}
function Owned-Id([string]$id){if($null -ne $main){$e=By-Id $main $id;if($null -ne $e -and $e.Current.ProcessId -eq $owner.Id){return $e}};foreach($w in (Owned-Windows)){if($w.Current.AutomationId -eq $id){return $w};$e=By-Id $w $id;if($null -ne $e -and $e.Current.ProcessId -eq $owner.Id){return $e}}}
function Assert-Owner($e){if($null -eq $e -or $e.Current.ProcessId -ne $owner.Id){throw 'Owned UIA element missing.'}}
function Invoke-Element($e){Assert-Owner $e;if(-not $e.Current.IsEnabled){throw 'Owned control disabled.'};$e.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()}
function Set-Value($e,[string]$text){Assert-Owner $e;$e.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).SetValue($text)}
function Read-Value($e){Assert-Owner $e;$e.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).Current.Value}
function Select-Element($e){Assert-Owner $e;$e.GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Select()}
function Apply-Manual([string]$query){
 Invoke-Element (By-Id $main 'OpenQueryGrouping');$script:editor=Wait-Until {Owned-Id 'QueryGroupingWindow'} 'editor opened'
 Select-Element (By-Id $script:editor 'AdvancedLogicTab')
 $combo=By-Id $script:editor 'QueryEditingMode';Assert-Owner $combo;$combo.GetCurrentPattern([Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
 Select-Element (Wait-Until {By-Name $script:editor '手写公式'} 'manual mode')
 $box=Wait-Until {By-Id $script:editor 'QueryManualFormula'} 'manual field';Set-Value $box $query;$value=Read-Value $box
 if($value -cne $query){throw 'Manual ValuePattern read-back differs.'}
 Wait-Until {if((Read-Value (By-Id $script:editor 'QueryGroupFormulaPreview')) -ceq $query -and (By-Id $script:editor 'QueryGroupApply').Current.IsEnabled){$true}} 'exact formula preview' | Out-Null
 Invoke-Element (By-Id $script:editor 'QueryGroupApply');Wait-Until {if($null -eq(Owned-Id 'QueryGroupingWindow')){$true}} 'editor closed' | Out-Null;$script:editor=$null
 $formula=(By-Id $main 'SearchQueryFormula').Current.Name
 if($formula -cne ('公式：'+$query)){throw ('Committed formula differs: '+$formula)}
 return [ordered]@{inputValue=$value;mainFormula=$formula}
}
function Search-Case([string]$name,[string]$root,[string]$query,[int]$files,[int]$hits,[string[]]$names){
 Set-Value (By-Name $main '搜索根目录') $root;$native=Apply-Manual $query;Invoke-Element (By-Name $main '开始搜索')
 Wait-Until {$p=(By-Id $main 'SearchProgressText').Current.Name;$s=(By-Id $main 'SearchStatusText').Current.Name;if((By-Name $main '开始搜索').Current.IsEnabled -and $s -like '已完成*' -and $p -match '^已扫描 '){$true}} ('completed '+$name) | Out-Null
 $p=(By-Id $main 'SearchProgressText').Current.Name;$s=(By-Id $main 'SearchStatusText').Current.Name;$normalized=$p -replace ',',''
 if($normalized -notmatch '已展示文件 ([0-9]+) · 已展示匹配 ([0-9]+) · 跳过/失败 ([0-9]+)$'){throw ('Missing completed counts: '+$p)}
 $actualFiles=[int]$Matches[1];$actualHits=[int]$Matches[2];$skipped=[int]$Matches[3]
 if($actualFiles -ne $files -or $actualHits -ne $hits -or $skipped -ne 0 -or $s -notlike '已完成*'){throw ('Unexpected '+$name+': '+$p+' / '+$s)}
 $rows=@((By-Id $main 'SearchResultsArea').FindAll($desc,[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty,[Windows.Automation.ControlType]::ListItem)))
 $actualNames=@($rows | ForEach-Object {[regex]::Match($_.Current.Name,'(?:none-only|none|sparse|overlap|unicode|all|short|extras)\.txt').Value} | Where-Object {$_} | Sort-Object -Unique)
 if($actualNames.Count -ne $names.Count -or @($actualNames | Where-Object {$_ -cnotin $names}).Count -ne 0){throw ('Displayed filenames differ: '+($actualNames -join ','))}
 $cases.Add([ordered]@{name=$name;passed=$true;root=$root;distinctTerms=@($query -split '\s+(?:OR|AND)\s+' | Sort-Object -Unique).Count;queryValue=$native.inputValue;mainFormula=$native.mainFormula;expectedFiles=$files;expectedHits=$hits;actualFiles=$actualFiles;actualHits=$actualHits;skippedFiles=$skipped;displayedFiles=$actualNames;progress=$p;status=$s})
}
$report=[ordered]@{passed=$false;checkedAtUtc=[datetime]::UtcNow.ToString('O');executable=$exe;exeSha256=(Get-FileHash -LiteralPath $exe).Hash;docSearchDll=$dll;docSearchDllSha256=(Get-FileHash -LiteralPath $dll).Hash;scriptSha256=(Get-FileHash -LiteralPath $PSCommandPath).Hash;runtimeRoot=$runtimeOverride;ownedProcessClosed=$false;normalCloseRequested=$false;sourceFilesUnchanged=$false;clipboardTouched=$false;globalInputUsed=$false;testOnlyUiaRuntime=$uiaRoot;sourceBefore=$before;sourceAfter=@();cases=@();preview=$null;error=$null;reportPath=Join-Path $owned 'results.json';notes='Native behavioral verification; UIA does not observe the internal branch. Optimized cases use 32 distinct terms and >=256 UTF-16 segment lengths; 4/8/2-term cases verify fallback. Non-overlap is per term: ababa yields one aba and one bab.'}
try{
 $launch=@{FilePath=$exe;ArgumentList=@('--settings',('"'+(Join-Path $owned 'settings.json')+'"'));PassThru=$true;WorkingDirectory=(Split-Path $exe);RedirectStandardOutput=(Join-Path $owned 'stdout.log');RedirectStandardError=(Join-Path $owned 'stderr.log')}
 if($runtimeOverride){$launch.Environment=@{DOTNET_ROOT=$runtimeOverride;DOTNET_ROOT_X64=$runtimeOverride;DOTNET_MULTILEVEL_LOOKUP='0'}}
 $owner=Start-Process @launch;$report.ownedProcessId=$owner.Id
 $main=Wait-Until {foreach($w in (Owned-Windows)){if($null -ne(By-Name $w '搜索根目录')){return $w}}} 'owned main window';Assert-Owner $main
 $owner.Refresh();$coreClr=@($owner.Modules|Where-Object ModuleName -eq 'coreclr.dll');if($coreClr.Count -ne 1){throw 'Expected one loaded CoreCLR.'};$report.coreClrPath=$coreClr[0].FileName
 if(-not $runtimeOverride -and $report.coreClrPath -ne (Join-Path (Split-Path $exe) 'coreclr.dll')){throw 'Self-contained runtime did not load from package.'}
 $fourOr='aba OR bab OR 西瓜 OR Ωmega';$fourAnd='aba AND bab AND 西瓜 AND Ωmega'
 # All 28 added literals are absent. Their OR branches preserve exact positive counts.
 $absentTerms=@(0..27 | ForEach-Object {'QueryAbsent'+$_.ToString('D2')})
 $thirtyTwoOr=$fourOr+' OR '+($absentTerms -join ' OR ')
 Search-Case 'prefilter-32-long-no-candidate' $none $thirtyTwoOr 0 0 @()
 Search-Case 'prefilter-32-late-sparse-overlap-unicode' $mixed $thirtyTwoOr 5 12 @('all.txt','overlap.txt','short.txt','sparse.txt','unicode.txt')
 Search-Case 'long-no-candidate-four-or' $none $fourOr 0 0 @()
 Search-Case 'sparse-overlap-unicode-short-four-or' $mixed $fourOr 5 12 @('all.txt','overlap.txt','short.txt','sparse.txt','unicode.txt')
 Search-Case 'four-and-qualification' $mixed $fourAnd 1 4 @('all.txt')
 Search-Case 'eight-or-supplementary-accented-unicode' $mixed 'aba OR bab OR 西瓜 OR Ωmega OR 🧭 OR école OR 東京 OR needleZ' 6 16 @('all.txt','extras.txt','overlap.txt','short.txt','sparse.txt','unicode.txt')
 Search-Case 'two-term-fallback' $mixed 'aba OR 西瓜' 5 8 @('all.txt','overlap.txt','short.txt','sparse.txt','unicode.txt')
 # Established built-in read-only preview action; no external reader action.
 $rows=@((By-Id $main 'SearchResultsArea').FindAll($desc,[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty,[Windows.Automation.ControlType]::ListItem)) | Where-Object {$_.Current.Name -match '\bshort\.txt\b'})
 if($rows.Count -ne 1){throw 'Expected one short.txt preview row.'};Invoke-Element (By-Name $rows[0] '定位并选中')
 $previewText=Wait-Until {Owned-Id 'PreviewTextBox'} 'read-only preview'
 $selectionStatus=Wait-Until {$e=Owned-Id 'PreviewSelectionStatus';if($null -ne $e -and $e.Current.Name -ceq '已选中 3 个字符 · 行 1 · 列 1'){$e.Current.Name}} 'verified SelectedText status'
 $previewValue=Read-Value $previewText;if($previewValue -cne 'aba 西瓜'){throw 'Preview document differs.'}
 $pattern=$null;$nativeSelection=$previewText.TryGetCurrentPattern([Windows.Automation.TextPattern]::Pattern,[ref]$pattern)
 $evidence='Application verified SelectedText status plus ValuePattern document read-back at line 1, column 1, length 3'
 if($nativeSelection){$selection=$pattern.GetSelection();if($selection.Count -ne 1 -or $selection[0].GetText(-1) -cne 'aba'){throw 'Native selected text differs.'};$evidence='Native UIA TextPattern selection plus application verified SelectedText status'}
 $report.preview=[ordered]@{passed=$true;file='short.txt';expectedSelectedText='aba';selectionStatus=$selectionStatus;documentValue=$previewValue;nativeTextPatternSupported=$nativeSelection;evidence=$evidence}
 $previewWindow=$previewText;while($previewWindow.Current.ControlType -ne [Windows.Automation.ControlType]::Window){$previewWindow=[Windows.Automation.TreeWalker]::ControlViewWalker.GetParent($previewWindow);if($null -eq $previewWindow){throw 'Preview window missing.'}}
 Assert-Owner $previewWindow
 if($previewWindow.Current.NativeWindowHandle -eq $main.Current.NativeWindowHandle){$report.preview.host='inline';Invoke-Element (By-Id $main 'CloseInlinePreview')}else{$report.preview.host='window';$previewWindow.GetCurrentPattern([Windows.Automation.WindowPattern]::Pattern).Close()}
 Wait-Until {if($null -eq(Owned-Id 'PreviewTextBox')){$true}} 'preview closed' | Out-Null
 $report.passed=$true
}catch{$report.error=$_.Exception.Message;throw}finally{
 if($null -ne $owner){$owner.Refresh();if(-not $owner.HasExited){
  foreach($id in @('QueryGroupingWindow','PreviewTextBox')){try{$dialog=Owned-Id $id;if($null -ne $dialog){while($dialog.Current.ControlType -ne [Windows.Automation.ControlType]::Window){$dialog=[Windows.Automation.TreeWalker]::ControlViewWalker.GetParent($dialog);if($null -eq $dialog){break}};if($null -ne $dialog -and $dialog.Current.ProcessId -eq $owner.Id -and $null -ne $main -and $dialog.Current.NativeWindowHandle -ne $main.Current.NativeWindowHandle){$dialog.GetCurrentPattern([Windows.Automation.WindowPattern]::Pattern).Close()}}}catch{$report.dialogCloseError=$_.Exception.Message}}
  $report.normalCloseRequested=$true;$owner.CloseMainWindow() | Out-Null;if(-not $owner.WaitForExit(10000)){$report.passed=$false;$report.error='Owned application did not close normally.'}
 };$owner.Refresh();$report.ownedProcessClosed=$owner.HasExited}
 $after=@(Source-State);$report.sourceAfter=$after;$report.sourceFilesUnchanged=(ConvertTo-Json -InputObject $after -Depth 6 -Compress) -ceq $beforeJson
 if(-not $report.sourceFilesUnchanged){$report.passed=$false;$report.error='Source SHA, length or modification time changed.'}
 if(-not $report.ownedProcessClosed -or -not $report.normalCloseRequested){$report.passed=$false;if($null -eq $report.error){$report.error='Normal owned shutdown was not completed.'}}
 $report.cases=$cases.ToArray();$report | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $report.reportPath -Encoding utf8NoBOM
 $report | ConvertTo-Json -Depth 10
}
if(-not $report.passed){throw $report.error}
