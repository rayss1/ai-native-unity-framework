function Assert-AiNativeNUnitReport {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string] $Path,
        [Parameter(Mandatory)][ValidateRange(1, 1000000)][int] $ExpectedPassed,
        [string[]] $RequiredFixtures = @(),
        [hashtable] $FixtureCounts = @{}
    )
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw "Missing NUnit report: $Path" }
    $settings = [Xml.XmlReaderSettings]::new()
    $settings.DtdProcessing = [Xml.DtdProcessing]::Prohibit
    $settings.XmlResolver = $null
    $reader = [Xml.XmlReader]::Create([IO.Path]::GetFullPath($Path), $settings)
    try {
        $document = [Xml.XmlDocument]::new()
        $document.XmlResolver = $null
        $document.Load($reader)
    } finally { $reader.Dispose() }
    $run = $document.'test-run'
    if (-not $run) { throw 'Missing NUnit test-run element' }
    $passed = [int] $run.passed
    $failed = [int] $run.failed
    $skipped = [int] $run.skipped
    $total = [int] $run.total
    $cases = @($document.SelectNodes('//test-case'))
    if ($run.result -ne 'Passed' -or $passed -ne $ExpectedPassed -or
        $failed -ne 0 -or $skipped -ne 0 -or $total -ne $passed -or
        $cases.Count -ne $total -or @($cases.fullname | Select-Object -Unique).Count -ne $total -or @($cases | Where-Object result -ne 'Passed').Count -ne 0) {
        throw "NUnit gate failed: expected=$ExpectedPassed result=$($run.result) total=$total passed=$passed failed=$failed skipped=$skipped cases=$($cases.Count)"
    }
    foreach ($fixture in $RequiredFixtures) {
        if (@($cases | Where-Object { ([string]$_.fullname).StartsWith($fixture + '.', [StringComparison]::Ordinal) }).Count -eq 0) {
            throw "NUnit report is missing required fixture: $fixture"
        }
    }
    $discovered = @{}
    foreach ($case in $cases) {
        $name = [string]$case.fullname
        $fixture = if ($case.ParentNode.GetAttribute('type') -eq 'TestFixture') { $case.ParentNode.GetAttribute('fullname') } else {
            # Parameterized cases sit inside TestMethod suites.
            $parent = $case.ParentNode
            while ($parent -and $parent.Name -ne 'test-run' -and $parent.GetAttribute('type') -ne 'TestFixture') { $parent = $parent.ParentNode }
            if ($parent -and $parent.GetAttribute('type') -eq 'TestFixture') { $parent.GetAttribute('fullname') } else { $name.Substring(0,$name.LastIndexOf('.')) }
        }
        $discovered[$fixture] = [int]$discovered[$fixture] + 1
    }
    if ($FixtureCounts.Count) {
        if ($discovered.Count -ne $FixtureCounts.Count) { throw 'NUnit fixture inventory differs from the selected source profile' }
        foreach ($fixture in $FixtureCounts.Keys) {
            if (-not $discovered.ContainsKey($fixture) -or $discovered[$fixture] -ne $FixtureCounts[$fixture]) { throw "NUnit fixture inventory mismatch: $fixture" }
        }
    }
    return [pscustomobject]@{
        Result = 'Passed'; Passed = $passed; Failed = $failed; Skipped = $skipped
        Path = [IO.Path]::GetFullPath($Path)
        Sha256 = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
        Fixtures = $discovered
    }
}

function Get-AiNativeEditModeFixtureCounts {
    param([ValidateSet('Baseline','Candidate','TerminalDelivery')][string]$Profile)
    $fixtures = @{
        'AiNative.Client.Application.Tests.BattleClientSessionTests'=32
        'AiNative.Client.Application.Tests.SimulationCadenceTests'=2
        'AiNative.Client.Application.Tests.TopologyClientFlowRecoveryTests'=5
        'AiNative.Client.Fantasy.Tests.FantasyKcpRealtimeTransportTests'=16
        'AiNative.Client.Prediction.Tests.ClientPredictionAdapterTests'=13
        'AiNative.Client.Prediction.Tests.PresentationCorrectionSmootherTests'=5
        'AiNative.Gameplay.Tests.AcceptanceSimulationVectorTests'=1
        'AiNative.Gameplay.Tests.ArenaGameplayTests'=8
        'AiNative.Gameplay.Tests.ClientPredictionTests'=7
        'AiNative.Gameplay.Tests.DeterminismContractTests'=3
        'AiNative.Gameplay.Tests.GameplayClockContractTests'=1
        'AiNative.Realtime.Tests.TransportContractTests'=2
    }
    if ($Profile -in @('Candidate','TerminalDelivery')) {
        $fixtures['AiNative.Client.Application.Tests.TopologyClientFlowRecoveryTests']=18
        $fixtures['AiNative.Client.Application.Tests.ArenaRemoteSessionTests']=2
        $fixtures['AiNative.Client.Prediction.Tests.ArenaPredictionSendTests']=8
        $fixtures['AiNative.Client.Prediction.Tests.ArenaRemotePresentationTests']=8
        $fixtures['AiNative.Client.Application.Tests.AndroidBattleClientBuildTests']=10
        $fixtures['AiNative.Client.Application.Tests.AndroidClientLaunchConfigurationTests']=11
    }
    if ($Profile -eq 'TerminalDelivery') {
        $fixtures['AiNative.Client.Application.Tests.TopologyClientFlowRecoveryTests']=19
        $fixtures['AiNative.Client.Application.Tests.TopologyTerminalReceiveTests']=6
        $fixtures['AiNative.Client.Fantasy.Tests.TerminalReceiveTests']=3
        $fixtures['AiNative.Client.Application.Tests.TerminalReceptionTests']=7
    }
    return $fixtures
}

function Get-AiNativeEvidenceFile {
    param([Parameter(Mandatory)][string]$Path)
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw "Missing evidence file: $Path" }
    return [pscustomobject]@{Path=[IO.Path]::GetFullPath($Path);Sha256=(Get-FileHash -LiteralPath $Path).Hash.ToLowerInvariant()}
}

function Assert-AiNativeTrxReport {
    param([Parameter(Mandatory)][string]$Path,[int]$ExpectedPassed=0,[string]$RequiredTest='')
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw "Missing TRX report: $Path" }
    $settings = [Xml.XmlReaderSettings]::new(); $settings.DtdProcessing = [Xml.DtdProcessing]::Prohibit; $settings.XmlResolver=$null
    $reader = [Xml.XmlReader]::Create([IO.Path]::GetFullPath($Path),$settings)
    try { $document=[Xml.XmlDocument]::new(); $document.XmlResolver=$null; $document.Load($reader) } finally { $reader.Dispose() }
    $summary=$document.SelectSingleNode('/*[local-name()="TestRun"]/*[local-name()="ResultSummary"]')
    if (-not $summary) { throw "TRX gate missing result summary: $Path" }
    $counters=$summary.SelectSingleNode('*[local-name()="Counters"]')
    if (-not $counters) { throw "TRX gate missing counters: $Path" }
    $cases=@($document.SelectNodes('/*[local-name()="TestRun"]/*[local-name()="Results"]/*[local-name()="UnitTestResult"]'))
    $counts=@{}
    foreach ($name in @('total','executed','passed','failed','notExecuted')) {
        $value=0L
        if (-not $counters.HasAttribute($name) -or -not [long]::TryParse($counters.GetAttribute($name),[Globalization.NumberStyles]::None,[Globalization.CultureInfo]::InvariantCulture,[ref]$value)) { throw "TRX gate missing or invalid counter $name`: $Path" }
        $counts[$name]=$value
    }
    foreach ($name in @('error','timeout','aborted','inconclusive','passedButRunAborted','notRunnable','disconnected','warning','inProgress','pending')) {
        if (-not $counters.HasAttribute($name)) { continue }
        $value=0L
        if (-not [long]::TryParse($counters.GetAttribute($name),[Globalization.NumberStyles]::None,[Globalization.CultureInfo]::InvariantCulture,[ref]$value) -or $value -ne 0) { throw "TRX gate nonzero or invalid counter $name`: $Path" }
    }
    $passed=$counts.passed; $total=$counts.total
    if ($summary.outcome -ne 'Completed' -or $passed -le 0 -or $counts.failed -ne 0 -or $total -ne $passed -or $counts.executed -ne $total -or $counts.notExecuted -ne 0 -or
        $cases.Count -ne $total -or @($cases | Where-Object outcome -ne 'Passed').Count -ne 0 -or
        @($cases.testId | Select-Object -Unique).Count -ne $cases.Count -or ($ExpectedPassed -gt 0 -and $passed -ne $ExpectedPassed)) { throw "TRX gate failed: $Path" }
    $tests=@()
    foreach ($case in $cases) {
        $definitions=@($document.SelectNodes('/*[local-name()="TestRun"]/*[local-name()="TestDefinitions"]/*[local-name()="UnitTest"]') | Where-Object id -eq $case.testId)
        if ($definitions.Count -ne 1) { throw "TRX gate has missing or duplicated test definition: $Path" }
        $method=$definitions[0].SelectSingleNode('*[local-name()="TestMethod"]')
        $tests += ([string]$method.className).Split(',')[0]+'.'+[string]$method.name
    }
    if ($RequiredTest -and ($tests.Count -ne 1 -or $tests[0] -ne $RequiredTest)) { throw "TRX missing required test: $RequiredTest" }
    $identity=Get-AiNativeEvidenceFile -Path $Path
    return [pscustomobject]@{Path=$identity.Path;Sha256=$identity.Sha256;Passed=$passed;Total=$total;Executed=$counts.executed;NotExecuted=$counts.notExecuted;Tests=$tests}
}

function Assert-AiNativeTopologyAcceptanceReport {
    param([Parameter(Mandatory)][string]$Path,[Parameter(Mandatory)][datetime]$StartedUtc)
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf) -or (Get-Item -LiteralPath $Path).LastWriteTimeUtc -lt $StartedUtc) { throw 'Missing or stale topology report' }
    $acceptance=Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
    if (($acceptance.exit -isnot [int] -and $acceptance.exit -isnot [long]) -or $acceptance.exit -ne 0 -or @($acceptance.evidence).Count -ne 8 -or @($acceptance.evidence | Where-Object passed -ne $true).Count) { throw 'Topology acceptance gate failed' }
    return Get-AiNativeEvidenceFile -Path $Path
}
