param(
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [int[]]$RootProcessIds,

    [ValidateRange(1, 10000)]
    [int]$Samples = 5,

    [ValidateRange(0, 3600)]
    [int]$IntervalSeconds = 2
)

$ErrorActionPreference = 'Stop'

function Get-ProcessTree {
    param([int]$RootProcessId, [object[]]$Processes)

    $ids = [System.Collections.Generic.HashSet[int]]::new()
    [void]$ids.Add($RootProcessId)
    do {
        $previousCount = $ids.Count
        foreach ($process in $Processes) {
            if ($ids.Contains([int]$process.ParentProcessId)) {
                [void]$ids.Add([int]$process.ProcessId)
            }
        }
    } while ($ids.Count -gt $previousCount)

    return @($Processes | Where-Object { $ids.Contains([int]$_.ProcessId) })
}

$initialProcesses = @(Get-CimInstance Win32_Process)
$roots = foreach ($rootId in $RootProcessIds) {
    $root = $initialProcesses | Where-Object { $_.ProcessId -eq $rootId } | Select-Object -First 1
    if ($null -eq $root) { throw "Root process $rootId is not running." }
    [pscustomobject]@{
        Id = $rootId
        Name = $root.Name
        CreationDate = $root.CreationDate
    }
}

for ($sample = 1; $sample -le $Samples; $sample++) {
    $processes = @(Get-CimInstance Win32_Process)
    $timestamp = [DateTimeOffset]::UtcNow.ToString('o')
    foreach ($root in $roots) {
        $current = $processes | Where-Object { $_.ProcessId -eq $root.Id } | Select-Object -First 1
        if ($null -eq $current -or $current.CreationDate -ne $root.CreationDate) {
            throw "Root process $($root.Id) exited or its PID was reused during measurement."
        }
        $tree = @(Get-ProcessTree -RootProcessId $root.Id -Processes $processes)
        $privateBytes = ($tree | Measure-Object -Property PrivatePageCount -Sum).Sum
        $workingBytes = ($tree | Measure-Object -Property WorkingSetSize -Sum).Sum
        [pscustomobject]@{
            TimestampUtc = $timestamp
            Sample = $sample
            RootProcessId = $root.Id
            RootName = $root.Name
            ProcessCount = $tree.Count
            PrivateMiB = [math]::Round($privateBytes / 1MB, 1)
            WorkingSetMiB = [math]::Round($workingBytes / 1MB, 1)
        }
    }
    if ($sample -lt $Samples -and $IntervalSeconds -gt 0) {
        Start-Sleep -Seconds $IntervalSeconds
    }
}
