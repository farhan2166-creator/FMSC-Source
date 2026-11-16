param([string]$Instance='(localdb)\MSSQLLocalDB')
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
$policy=Get-Content (Join-Path $root 'Pages/Students/Admissions/MissingPlacementWindows.sql') -Raw
# Run the production selection logic against temporary synthetic tables only.
$policy=$policy.Replace('dbo.Students','#Students').Replace('dbo.Enrollments','#Enrollments').Replace('dbo.AcademicYears','#AcademicYears').Replace('dbo.Classes','#Classes').Replace('dbo.AcademicPrograms','#AcademicPrograms').Replace('dbo.CampusClassSections','#CampusClassSections')
$save=Get-Content (Join-Path $root 'Pages/Students/Admissions/PlacementCorrection.sql') -Raw
$guard=[regex]::Match($save,'(?s) IF @EnrollmentId=0 AND NOT EXISTS.*?THROW 59622,.*?;').Value
if(-not $guard){throw 'Production history save guard not found.'}
$billingGuard=[regex]::Match($save,'(?s) IF @EnrollmentId=0 AND @BillingStartDate IS NOT NULL.*?THROW 59623,.*?;').Value
if(-not $billingGuard){throw 'Production historical billing guard not found.'}
$departureGuard=[regex]::Match($save,'(?s) DECLARE @CanRecordDeparture bit=0;.*?THROW 59624,.*?;').Value
$departureWrite=[regex]::Match($save,'(?s) IF @RecordHistoricalDeparture=1\s+BEGIN.*?\r?\n END;').Value
if(-not $departureGuard -or -not $departureWrite){throw 'Production historical departure logic not found.'}
$departureGuard=$departureGuard.Replace('dbo.Students','#Students').Replace('dbo.Enrollments','#Enrollments').Replace('dbo.StudentPostingHolds','#StudentPostingHolds')
$departureWrite=$departureWrite.Replace('dbo.Students','#Students').Replace('dbo.StudentStatusHistory','#StudentStatusHistory')
$checks=Get-Content (Join-Path $root 'Validation/MissingPlacementPolicyChecks.sql') -Raw
$checks=$checks.Replace('-- POLICY',$policy).Replace('-- GUARD',$guard).Replace('-- BILLING_GUARD',$billingGuard)
$checks=$checks.Replace('-- DEPARTURE_GUARD',$departureGuard).Replace('-- DEPARTURE_WRITE',$departureWrite)
$tempSql=Join-Path ([IO.Path]::GetTempPath()) ('FMSC_MissingPlacement_'+[guid]::NewGuid().ToString('N')+'.sql')
try {
 [IO.File]::WriteAllText($tempSql,$checks)
 & sqlcmd -S $Instance -E -d master -b -l 15 -t 60 -i $tempSql
 if($LASTEXITCODE -ne 0){throw 'Missing-placement policy checks failed.'}
} finally {Remove-Item -LiteralPath $tempSql -ErrorAction SilentlyContinue}
