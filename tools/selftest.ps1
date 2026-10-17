<#
.SYNOPSIS
  Runs TerraBot's in-game self-test in an isolated tModLoader save directory and prints the results.

.DESCRIPTION
  1. Builds the mod (unless -NoBuild).
  2. Prepares .selftest\SaveData: TerraBot.tmod as the only enabled mod, plus COPIES of one player and one world
     from your real saves (the originals are only read), and your config/input profile so no first-run screens show.
  3. Launches tModLoader with -tmlsavedirectory <that folder> -skipselect "<player>:<world>" -terrabotselftest <names>.
     The mod builds each scenario in an arena above spawn, runs the bot, logs SELFTEST lines and quits the game.
  4. Prints the SELFTEST lines from tModLoader-Logs\client.log. Exit code = number of failed scenarios.

  The self-test keeps the game running while its window is not focused (vanilla singleplayer pauses then), so
  other windows can be used meanwhile.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File tools\selftest.ps1
  powershell -ExecutionPolicy Bypass -File tools\selftest.ps1 -Scenarios bridge_wide_dry_pit,out_of_blocks_replans
#>
param(
	[string]$Scenarios = "all",
	[string]$Player = "Pepi",
	[string]$World = "Enchanting Outskirts",
	[string]$WorldFile = "Enchanting_Outskirts",
	[string]$TmlDir = "C:\Program Files (x86)\Steam\steamapps\common\tModLoader",
	[int]$TimeoutMinutes = 20,
	[switch]$NoBuild,
	# Run the game as fast as the PC allows instead of 60 ticks a second (needs Frame Skip Off or Subtle).
	[switch]$Fast,
	# With -Fast: draw only every N-th frame (the game logic still runs every tick).
	[int]$DrawEvery = 1,
	# Debug experiment: finding ore, crystals, stations and chests ignores the map (no exploring needed).
	[switch]$XRay,
	# Real-world runs: save the world and player at the late milestones (see SelfTestSystem.CheckpointMilestones) into
	# .selftest\SaveData\Checkpoints\<milestone>.
	[switch]$Checkpoints,
	# Start from a checkpoint folder (its .plr/.tplr/.wld/.twld replace the copies of the originals).
	[string]$FromCheckpoint = "",
	# Real-world runs: a game-time budget in days instead of the scenario's own.
	[double]$BudgetDays = 0,
	# Real-world runs on a Journey character: no research or duplication (the bot plays it as on Classic).
	[switch]$NoJourney,
	# Other mods to load beside TerraBot, by internal name (e.g. UpgradedResearch): the .tmod from your Mods folder, else the
	# newest one in the Steam workshop folder, with your saved settings for it (ModConfigs\<name>_*.json). Default: none.
	[string[]]$Mods = @(),
	# The planner's scheduler (ProgressionPlanner.Schedulers): restart (stages from the top every pass) or operation (a continuing
	# job owns the bot). Default: the build's own (operation).
	[string]$Scheduler = ""
)
$ErrorActionPreference = "Stop"

$repo = Split-Path $PSScriptRoot -Parent
$saves = Join-Path ([Environment]::GetFolderPath("MyDocuments")) "My Games\Terraria\tModLoader"
$testDir = Join-Path $repo ".selftest\SaveData"
$logDir = Join-Path $TmlDir "tModLoader-Logs"

$running = Get-CimInstance Win32_Process -Filter "Name='dotnet.exe'" | Where-Object { $_.CommandLine -like "*tModLoader.dll*" -and $_.CommandLine -notlike "*-build*" }
if ($running) { throw "tModLoader is already running (pid $($running.ProcessId -join ', ')). Close it first." }

if (-not $NoBuild) {
	Write-Host "Building mod..."
	Push-Location $repo
	try { dotnet build -c Release --nologo -v q | Out-Host; if ($LASTEXITCODE -ne 0) { throw "dotnet build failed" } }
	finally { Pop-Location }
}

Write-Host "Preparing $testDir"
foreach ($d in "Players", "Worlds") { Remove-Item -Recurse -Force (Join-Path $testDir $d) -ErrorAction SilentlyContinue }
foreach ($d in "Mods", "Players", "Worlds") { New-Item -ItemType Directory -Force (Join-Path $testDir $d) | Out-Null }
Copy-Item (Join-Path $saves "Mods\TerraBot.tmod") (Join-Path $testDir "Mods\TerraBot.tmod") -Force
$enabled = @("TerraBot")
$workshop = Join-Path (Split-Path (Split-Path $TmlDir -Parent) -Parent) "workshop\content\1281930"
New-Item -ItemType Directory -Force (Join-Path $testDir "ModConfigs") | Out-Null
foreach ($m in $Mods) {
	$tmod = Join-Path $saves "Mods\$m.tmod"
	if (-not (Test-Path $tmod)) {
		$tmod = Get-ChildItem $workshop -Recurse -Filter "$m.tmod" -ErrorAction SilentlyContinue |
			Sort-Object { try { [version]$_.Directory.Name } catch { [version]"0.0" } } | Select-Object -Last 1 -ExpandProperty FullName
	}
	if (-not $tmod) { throw "Mod $m not found in $saves\Mods or $workshop" }
	Copy-Item $tmod (Join-Path $testDir "Mods\$m.tmod") -Force
	foreach ($sub in "", "Server") {
		$from = Join-Path $saves "ModConfigs\$sub"
		if (Test-Path $from) {
			New-Item -ItemType Directory -Force (Join-Path $testDir "ModConfigs\$sub") | Out-Null
			Get-ChildItem $from -Filter "${m}_*.json" -File | ForEach-Object { Copy-Item $_.FullName (Join-Path $testDir "ModConfigs\$sub") -Force }
		}
	}
	Write-Host "Also loading $m from $tmod"
	$enabled += $m
}
Set-Content -Encoding ascii (Join-Path $testDir "Mods\enabled.json") ("[" + (($enabled | ForEach-Object { '"' + $_ + '"' }) -join ",") + "]")
foreach ($f in "config.json", "input profiles.json") {
	$src = Join-Path $saves $f
	if (Test-Path $src) { Copy-Item $src (Join-Path $testDir $f) -Force }
}
foreach ($ext in ".plr", ".tplr") {
	$src = Join-Path $saves "Players\$Player$ext"
	if (Test-Path $src) { Copy-Item $src (Join-Path $testDir "Players") -Force }
}
foreach ($ext in ".wld", ".twld") {
	$src = Join-Path $saves "Worlds\$WorldFile$ext"
	if (Test-Path $src) { Copy-Item $src (Join-Path $testDir "Worlds") -Force }
}
# Where a checkpoint was saved (its start.txt, SelfTestSystem.SaveCheckpoint): runs that resume there read it.
$startFile = Join-Path $testDir "terrabot_start.txt"
Remove-Item $startFile -ErrorAction SilentlyContinue
if ($FromCheckpoint -ne "") {
	if (-not (Test-Path $FromCheckpoint)) { throw "Checkpoint folder $FromCheckpoint not found" }
	Get-ChildItem $FromCheckpoint -Include *.plr, *.tplr -File -Recurse | ForEach-Object { Copy-Item $_.FullName (Join-Path $testDir "Players") -Force }
	Get-ChildItem $FromCheckpoint -Include *.wld, *.twld -File -Recurse | ForEach-Object { Copy-Item $_.FullName (Join-Path $testDir "Worlds") -Force }
	# The player's map of this world (Players\<player>\<world id>.map): the bot sees only what the map has revealed.
	$mapDir = Join-Path $testDir "Players\$Player"
	New-Item -ItemType Directory -Force $mapDir | Out-Null
	Get-ChildItem $FromCheckpoint -Include *.map -File -Recurse | ForEach-Object { Copy-Item $_.FullName $mapDir -Force }
	Get-ChildItem $FromCheckpoint -Filter "start.txt" -File -Recurse | Select-Object -First 1 | ForEach-Object { Copy-Item $_.FullName $startFile -Force }
	Write-Host "Starting from checkpoint $FromCheckpoint"
}
if ($Checkpoints) { Remove-Item (Join-Path $testDir "Checkpoints") -Recurse -Force -ErrorAction SilentlyContinue }
if (-not (Test-Path (Join-Path $testDir "Players\$Player.plr"))) { throw "Player file $Player.plr not found in $saves\Players" }
if (-not (Test-Path (Join-Path $testDir "Worlds\$WorldFile.wld"))) { throw "World file $WorldFile.wld not found in $saves\Worlds" }

$tmlArgs = @("tModLoader.dll", "-tmlsavedirectory", "`"$testDir`"", "-skipselect", "`"${Player}:${World}`"",
	"-terrabotselftest", $Scenarios, "-steamworkshopfolder", "none")
if ($Fast) { $tmlArgs += @("-terrabotfast", "$DrawEvery") }
if ($XRay) { $tmlArgs += @("-terrabotxray", "1") }
if ($Checkpoints) { $tmlArgs += @("-terrabotcheckpoints", "1") }
if ($BudgetDays -gt 0) { $tmlArgs += @("-terrabotdays", ([string]$BudgetDays).Replace(",", ".")) }
if ($NoJourney) { $tmlArgs += @("-terrabotnojourney", "1") }
if ($Scheduler) { $tmlArgs += @("-terrabotscheduler", $Scheduler) }
Write-Host "Launching tModLoader:  $($tmlArgs -join ' ')"
$started = Get-Date
$proc = Start-Process -FilePath (Join-Path $TmlDir "dotnet\dotnet.exe") -ArgumentList $tmlArgs -WorkingDirectory $TmlDir -PassThru
if (-not $proc.WaitForExit($TimeoutMinutes * 60 * 1000)) {
	Write-Warning "Timed out after $TimeoutMinutes min; killing tModLoader."
	$proc.Kill()
}
Write-Host ("tModLoader exited after {0:0} s" -f ((Get-Date) - $started).TotalSeconds)

# tModLoader writes client.log, or client2.log etc. when that file is locked by another reader, so take the newest
# client*.log written since launch.
$log = Get-ChildItem $logDir -Filter "client*.log" | Where-Object { $_.LastWriteTime -ge $started } |
	Sort-Object LastWriteTime -Descending | Select-Object -First 1 -ExpandProperty FullName
if (-not $log) { throw "No client*.log written since launch in $logDir" }
Write-Host "Reading $log"
$lines = Select-String -Path $log -Pattern "SELFTEST" -CaseSensitive | ForEach-Object { $_.Line -replace '^\[[^\]]*\] \[[^\]]*\] \[TerraBot\]: ', '' }
if (-not $lines) {
	Write-Warning "No SELFTEST lines in the log. Last log lines:"
	Get-Content $log -Tail 40 | Out-Host
	exit 99
}
$lines | Out-Host
$failed = @($lines | Where-Object { $_ -like "SELFTEST FAIL*" }).Count
$summary = $lines | Where-Object { $_ -like "SELFTEST SUMMARY*" }
if (-not $summary) { Write-Warning "No summary line: the run did not finish." ; exit 98 }
exit $failed
