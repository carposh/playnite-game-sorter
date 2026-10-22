# Game Sorter - Playnite side.
# GameSorter.exe scans your game folders and writes the changes to
# %APPDATA%\GameSorter\pending.json. This applies them when Playnite starts
# and writes what happened to result.json so the app can show it.

$script:SteamPluginId = [guid]'cb91dfc9-b977-43bf-8e70-55f46e410fab'
$script:DataDir = Join-Path $env:APPDATA 'GameSorter'

function OnApplicationStarted {
    param($evnArgs)
    Invoke-GameSorterApply $false
}

function GetMainMenuItems {
    param($getMainMenuItemsArgs)
    $item = New-Object Playnite.SDK.Plugins.ScriptMainMenuItem
    $item.Description = 'Apply pending Game Sorter changes'
    $item.FunctionName = 'Invoke-GameSorterMenu'
    $item.MenuSection = '@Game Sorter'
    return $item
}

function Invoke-GameSorterMenu {
    param($scriptMainMenuItemActionArgs)
    Invoke-GameSorterApply $true
}

# ---------------------------------------------------------------- helpers ---

function Get-NormName([string]$s) {
    if (-not $s) { return '' }
    return $s.ToLowerInvariant() -replace '&|\band\b', '' -replace '[^a-z0-9]', ''
}

function Get-SortingName([string]$name) {
    if ($name -match '^(?i)(the|a|an)\s+(.+)$') { return $Matches[2] + ', ' + $Matches[1] }
    return $null
}

# Find an item (genre, category, source...) by name, or create it.
function Get-OrAdd($collection, [string]$name) {
    $hit = $collection | Where-Object { $_.Name -eq $name } | Select-Object -First 1
    if ($hit) { return $hit }
    return $collection.Add($name)
}

function New-IdList($collection, $names) {
    $list = New-Object 'System.Collections.Generic.List[Guid]'
    foreach ($n in @($names)) {
        if (-not $n) { continue }
        $id = (Get-OrAdd $collection ([string]$n)).Id
        if (-not $list.Contains($id)) { $list.Add($id) }
    }
    return , $list
}

function Get-PlayAction($game) {
    if (-not $game.GameActions) { return $null }
    return $game.GameActions | Where-Object { $_.IsPlayAction -and $_.Type -eq [Playnite.SDK.Models.GameActionType]::File } | Select-Object -First 1
}

function Find-Existing($e) {
    $db = $PlayniteApi.Database
    $paths = @(@($e.exe, $e.oldExe) | Where-Object { $_ } | ForEach-Object { $_.ToLowerInvariant() })
    $dirs = @(@($e.dir, $e.oldDir) | Where-Object { $_ } | ForEach-Object { $_.TrimEnd('\').ToLowerInvariant() })
    $local = @($db.Games | Where-Object { $_.PluginId -eq [guid]::Empty })
    foreach ($g in $local) {
        if ($g.InstallDirectory -and ($dirs -contains $g.InstallDirectory.TrimEnd('\').ToLowerInvariant())) { return $g }
        $a = Get-PlayAction $g
        if ($a -and $a.Path -and ($paths -contains $a.Path.ToLowerInvariant())) { return $g }
    }
    if ($e.appId) {
        foreach ($g in $local) {
            foreach ($l in @($g.Links)) { if ($l -and $l.Url -match "store\.steampowered\.com/app/$($e.appId)(/|$)") { return $g } }
        }
    }
    $n = Get-NormName $e.name
    foreach ($g in $local) { if ((Get-NormName $g.Name) -eq $n) { return $g } }
    return $null
}

function Set-PlayAction($game, $e) {
    $a = Get-PlayAction $game
    if (-not $a) {
        if (-not $game.GameActions) { $game.GameActions = New-Object 'System.Collections.ObjectModel.ObservableCollection[Playnite.SDK.Models.GameAction]' }
        $a = New-Object Playnite.SDK.Models.GameAction
        $a.Name = 'Play'
        $a.Type = [Playnite.SDK.Models.GameActionType]::File
        $a.IsPlayAction = $true
        $game.GameActions.Add($a)
    }
    $a.Path = $e.exe
    $a.WorkingDir = $e.workDir
}

function Add-ImageFile($game, [string]$path) {
    if (-not $path -or -not (Test-Path -LiteralPath $path)) { return $null }
    return $PlayniteApi.Database.AddFile($path, $game.Id)
}

function Set-Metadata($game, $e, [bool]$isNew) {
    $db = $PlayniteApi.Database
    if (-not $game.SortingName) { $game.SortingName = Get-SortingName $game.Name }
    if ($e.description -and ($isNew -or -not $game.Description)) { $game.Description = $e.description }
    if ($e.genres -and ($isNew -or -not $game.GenreIds)) { $game.GenreIds = New-IdList $db.Genres $e.genres }
    if ($e.developers -and ($isNew -or -not $game.DeveloperIds)) { $game.DeveloperIds = New-IdList $db.Companies $e.developers }
    if ($e.publishers -and ($isNew -or -not $game.PublisherIds)) { $game.PublisherIds = New-IdList $db.Companies $e.publishers }
    if ($e.features -and ($isNew -or -not $game.FeatureIds)) { $game.FeatureIds = New-IdList $db.Features $e.features }
    if ($e.releaseDate -and ($isNew -or -not $game.ReleaseDate)) {
        $dt = [datetime]::ParseExact($e.releaseDate, 'yyyy-MM-dd', [Globalization.CultureInfo]::InvariantCulture)
        $game.ReleaseDate = New-Object Playnite.SDK.Models.ReleaseDate $dt
    }
    if (-not $game.CoverImage) { $f = Add-ImageFile $game $e.cover; if ($f) { $game.CoverImage = $f } }
    if (-not $game.BackgroundImage) { $f = Add-ImageFile $game $e.background; if ($f) { $game.BackgroundImage = $f } }
    if (-not $game.Icon) { $f = Add-ImageFile $game $e.icon; if ($f) { $game.Icon = $f } }
    if ($e.appId) {
        if (-not $game.Links) { $game.Links = New-Object 'System.Collections.ObjectModel.ObservableCollection[Playnite.SDK.Models.Link]' }
        if (-not ($game.Links | Where-Object { $_.Name -eq 'Steam Store' })) {
            $game.Links.Add((New-Object Playnite.SDK.Models.Link 'Steam Store', "https://store.steampowered.com/app/$($e.appId)/"))
        }
    }
    if (-not $game.GenreIds -and -not $game.CoverImage) {
        $game.TagIds = New-IdList $db.Tags @('Needs metadata')
    }
}

function Update-Categories($lines, $counts) {
    $db = $PlayniteApi.Database
    foreach ($g in @($db.Games)) {
      try {
        $src = if ($g.SourceId -ne [guid]::Empty) { $s = $db.Sources.Get($g.SourceId); if ($s) { $s.Name } } else { $null }
        $cats = @(if ($src -eq 'PC Folder') { 'PC Folder Games' } elseif ($src) { "$src Games" } else { 'Other Games' })
        if ($g.Name -match '(?i)\bdemo\b|playtest') { $cats += 'Demos' }
        # (no 'if' expression here: PowerShell would unwrap a one-item list)
        $ids = $g.CategoryIds
        if ($null -eq $ids) { $ids = New-Object 'System.Collections.Generic.List[Guid]' }
        $changed = $false
        foreach ($c in $cats) { $id = (Get-OrAdd $db.Categories $c).Id; if (-not $ids.Contains($id)) { $ids.Add($id); $changed = $true } }
        if ($changed -or -not $g.CategoryIds) { $g.CategoryIds = $ids; $db.Games.Update($g) }
      } catch { $counts.errors++; $lines.Add("ERROR       sorting $($g.Name) into a category: $($_.Exception.Message)") }
    }
}

# ------------------------------------------------------------------ apply ---

function Invoke-ApplyOne($e, $source, $pc, $lines, $counts) {
    $db = $PlayniteApi.Database

    if ($e.action -eq 'missing') {
        $g = Find-Existing $e
        if ($g -and $g.IsInstalled) { $g.IsInstalled = $false; $db.Games.Update($g) }
        $counts.missing++
        $lines.Add("MISSING     $($e.name)  - folder is gone ($($e.oldDir)), marked as not installed")
        return
    }

    if ($e.action -eq 'add' -and $e.appId) {
        $owned = $db.Games | Where-Object { $_.PluginId -eq $script:SteamPluginId -and $_.GameId -eq [string]$e.appId } | Select-Object -First 1
        if ($owned) {
            $counts.skipped++
            $lines.Add("SKIPPED     $($e.name)  - already in your Steam library")
            return
        }
    }

    $g = Find-Existing $e
    $isNew = -not $g
    if ($isNew) {
        $g = New-Object Playnite.SDK.Models.Game ([string]$e.name)
        $g.SourceId = $source.Id
        if ($pc) { $g.PlatformIds = New-Object 'System.Collections.Generic.List[Guid]'; $g.PlatformIds.Add($pc.Id) }
        $db.Games.Add($g) | Out-Null
    }
    $oldAction = Get-PlayAction $g
    $oldExe = if ($oldAction) { $oldAction.Path } else { $null }

    Set-PlayAction $g $e
    $g.InstallDirectory = $e.dir
    $g.IsInstalled = $true
    Set-Metadata $g $e $isNew
    $db.Games.Update($g)

    if ($isNew) {
        $counts.added++
        $lines.Add("ADDED       $($g.Name)  $($e.note)")
    } elseif ($e.action -eq 'update') {
        $counts.updated++
        $lines.Add("UPDATED     $($g.Name)  $($e.note)")
    } elseif ($oldExe -and $oldExe -ne $e.exe) {
        $counts.updated++
        $lines.Add("UPDATED     $($g.Name)  now uses $($e.exe)  (was $oldExe)")
    } else {
        $counts.same++
    }
}

function Invoke-GameSorterApply([bool]$fromMenu) {
    $pending = Join-Path $script:DataDir 'pending.json'
    if (-not (Test-Path -LiteralPath $pending)) {
        if ($fromMenu) { $PlayniteApi.Dialogs.ShowMessage('No pending changes. Run GameSorter.exe to scan your game folders.', 'Game Sorter') | Out-Null }
        return
    }

    $lines = New-Object System.Collections.Generic.List[string]
    $counts = @{ added = 0; updated = 0; same = 0; skipped = 0; missing = 0; errors = 0 }
    $runId = ''
    try {
        $job = [IO.File]::ReadAllText($pending, [Text.Encoding]::UTF8) | ConvertFrom-Json
        $runId = $job.runId
        $db = $PlayniteApi.Database
        $source = Get-OrAdd $db.Sources 'PC Folder'
        $pc = $db.Platforms | Where-Object { $_.SpecificationId -eq 'pc_windows' } | Select-Object -First 1
        foreach ($e in @($job.games)) {
            try { Invoke-ApplyOne $e $source $pc $lines $counts }
            catch { $counts.errors++; $lines.Add("ERROR       $($e.name): $($_.Exception.Message)") }
        }
        Update-Categories $lines $counts
    } catch {
        $counts.errors++
        $lines.Add("ERROR       $($_.Exception.Message)")
    }

    $result = [ordered]@{ runId = $runId; finished = (Get-Date).ToString('s'); counts = $counts; lines = @($lines) }
    [IO.File]::WriteAllText((Join-Path $script:DataDir 'result.json'), ($result | ConvertTo-Json -Depth 4), (New-Object Text.UTF8Encoding $false))
    Remove-Item -LiteralPath $pending -ErrorAction SilentlyContinue

    if ($fromMenu) {
        $PlayniteApi.Dialogs.ShowMessage(
            "Added: $($counts.added)   Updated: $($counts.updated)   Missing: $($counts.missing)   Skipped: $($counts.skipped)   Errors: $($counts.errors)",
            'Game Sorter') | Out-Null
    }
}
