param([Parameter(Mandatory)][string]$Path)
$errs = $null
$null = [System.Management.Automation.PSParser]::Tokenize((Get-Content $Path -Raw), [ref]$errs)
if ($errs.Count -eq 0) { Write-Output "PS SYNTAX OK: $Path" }
else { $errs | ForEach-Object { Write-Output ("ERR line " + $_.Token.StartLine + ": " + $_.Message) } }
