param([int]$SummaryPort=4532,[int]$WeeklyPort=4533)
$ErrorActionPreference='Stop'
$taskEvidence=$PSScriptRoot
$taskRepo=(Resolve-Path (Join-Path $PSScriptRoot '../../../../..')).Path
$taskWorkspace=Split-Path (Split-Path $taskRepo -Parent) -Parent
$taskCheckout=Join-Path $taskWorkspace '.tmp/back-f4-412-e0a9c29'
$taskExpectedSha='e0a9c29dfbf551eaa3d51a97173fe72b55a0781b'
if((git -C $taskCheckout rev-parse HEAD) -ne $taskExpectedSha){throw 'Checkout aislado no coincide con el SHA de esta evidencia'}
foreach($taskPort in @($SummaryPort,$WeeklyPort)){
    if(Get-NetTCPConnection -LocalPort $taskPort -State Listen -ErrorAction SilentlyContinue){throw "Puerto QA $taskPort ocupado; no se detiene ningún proceso existente"}
}
$taskPostman=(Get-Command postman.exe).Source
$taskStarted=@()
try {
    foreach($taskSpec in @(@{name='patient-summary';port=$SummaryPort},@{name='weekly-suggestions';port=$WeeklyPort})){
        $taskSim=Join-Path $taskRepo "postman/simulations/$($taskSpec.name).sim.yaml"
        $taskProc=Start-Process -FilePath $taskPostman -ArgumentList @('simulation','run',$taskSim,'--port',$taskSpec.port,'--output','ndjson','--no-history') -WorkingDirectory $taskRepo -WindowStyle Hidden -RedirectStandardOutput "$taskEvidence/$($taskSpec.name)-rerun.ndjson" -RedirectStandardError "$taskEvidence/$($taskSpec.name)-rerun.stderr.log" -PassThru
        $taskStarted += [pscustomobject]@{pid=$taskProc.Id;simulation=$taskSim;port=$taskSpec.port}
    }
    foreach($taskService in $taskStarted){
        $taskReady=$false
        for($taskAttempt=0;$taskAttempt -lt 15;$taskAttempt++){
            if(Get-NetTCPConnection -LocalPort $taskService.port -State Listen -ErrorAction SilentlyContinue){$taskReady=$true;break}
            Start-Sleep -Seconds 1
        }
        if(-not $taskReady){throw 'No arrancó el stand-in propio; no se probará contra el proveedor real'}
    }
    $taskRaw=[IO.File]::ReadAllText((Join-Path $taskWorkspace 'coppAddresdBack/src/CoppAddresd.Api/appsettings.json'))
    $taskMatch=[regex]::Match($taskRaw,'"DefaultConnection"\s*:\s*"([^"\r\n]+)"')
    if(-not $taskMatch.Success){throw 'Falta conexión PostgreSQL local'}
    $env:COP_TEST_DB_CONNECTION=$taskMatch.Groups[1].Value
    $taskRaw=$null;$taskMatch=$null
    $env:COP_PATIENT_SUMMARY_SIM_URL="http://127.0.0.1:$SummaryPort"
    $env:COP_WEEKLY_SUGGESTIONS_SIM_URL="http://127.0.0.1:$WeeklyPort"
    # Los binarios provienen del build 4.12; no se toca el runtime de API5122.
    dotnet test "$taskCheckout/tests/CoppAddresd.UnitTests" --no-build -p:OutputPath=bin/Validation412/ --filter 'FullyQualifiedName~Cliente_ContratoCompleto_BytesConsentimiento_InsuficienciaY503_ConSimulacionOpcional|FullyQualifiedName~WeeklyCliente_BytesConsentimiento_OkInsuficiente503_SimulacionOpcional' --results-directory $taskEvidence --logger 'trx;LogFileName=rerun-clients.trx' -v quiet
    if($LASTEXITCODE -ne 0){throw 'Fallaron los clientes HTTP; conservar resultado como fallo'}
    dotnet test "$taskCheckout/tests/CoppAddresd.IntegrationTests" --no-build -p:OutputPath=bin/Validation412/ --filter 'FullyQualifiedName~NueveTests_EventoPostCommit_InscripcionPendienteYPropuestaAtomicas_Idempotentes|FullyQualifiedName~PatientSummary_IAFallida_InsuficienteTimeoutYCancelacion_NoPersistenNiBorranBorradorPrevio|FullyQualifiedName~WeeklyAI_FallosIAInsuficienciaCancelacionYConsentimiento_NoPersistenNiAplican' --results-directory $taskEvidence --logger 'trx;LogFileName=rerun-postgres.trx' -v quiet
    if($LASTEXITCODE -ne 0){throw 'Falló PostgreSQL aislado; no declarar degradación verificada'}
}
finally {
    Remove-Item Env:COP_TEST_DB_CONNECTION,Env:COP_PATIENT_SUMMARY_SIM_URL,Env:COP_WEEKLY_SUGGESTIONS_SIM_URL -ErrorAction SilentlyContinue
    foreach($taskRoot in $taskStarted){
        $taskAll=Get-CimInstance Win32_Process
        $taskRootNode=$taskAll|Where-Object ProcessId -eq $taskRoot.pid
        if(-not $taskRootNode){continue}
        if($taskRootNode.CommandLine -notlike "*$($taskRoot.simulation)*"){Write-Warning 'Identidad de raíz distinta; no se detiene';continue}
        $taskQueue=@([int]$taskRoot.pid);$taskOwned=@()
        while($taskQueue.Count){
            $taskId=$taskQueue[0];$taskQueue=@($taskQueue|Select-Object -Skip 1)
            $taskNode=$taskAll|Where-Object ProcessId -eq $taskId
            if($taskNode){$taskOwned += $taskNode;$taskQueue += @($taskAll|Where-Object ParentProcessId -eq $taskId|ForEach-Object {[int]$_.ProcessId})}
        }
        foreach($taskNode in @($taskOwned|Sort-Object CreationDate -Descending)){
            $taskLive=Get-CimInstance Win32_Process -Filter "ProcessId=$($taskNode.ProcessId)" -ErrorAction SilentlyContinue
            if($taskLive -and $taskLive.ExecutablePath -eq $taskNode.ExecutablePath -and $taskLive.CreationDate -eq $taskNode.CreationDate){Stop-Process -Id $taskLive.ProcessId -Force -ErrorAction SilentlyContinue}
        }
    }
}
