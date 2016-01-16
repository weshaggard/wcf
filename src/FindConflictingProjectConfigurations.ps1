msbuild /nologo /v:quiet src.builds /t:build /flp:v=detailed`;LogFile=dumptargets.log
 
$targets = gc dumptargets.log | ? { $_.Contains("DumpTargets>") -or ($_.Contains("is building") -and ($_.Contains("default target") -or $_.Contains("Build target") -or $_.Contains("BuildAndTest target"))) }

$ht = new-object Hashtable
$duplicates = @();
$foundConflict = $false;
$lastIsBuilding = "";

foreach($target in $targets)
{
	#"->" + $target

	if ($target.Contains("is building"))
	{
		$lastIsBuilding = $target;
		continue;
	}

	if ($ht.Contains($target))
	{
		$buildingProject = $ht[$target];

		"Conflict:"
		"$target"
		"1> $buildingProject"
		"2> $lastIsBuilding"
		"`n"
		$foundConflict = $true;
	}
	else
	{
		$ht.Add($target, $lastIsBuilding);
	}
}

if ($foundConflict -eq $false)
{
"Found no conflicts";
} 
