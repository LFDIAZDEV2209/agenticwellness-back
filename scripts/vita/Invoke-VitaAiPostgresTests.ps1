param(
    [string]$AiWorktree = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../../vita-ai')),
    [switch]$ProvisioningOnly
)

$ErrorActionPreference = 'Stop'
$vitaMarker = Join-Path $env:TEMP 'vita-back-pg-path.txt'
if (-not (Test-Path -LiteralPath $vitaMarker)) { throw 'No existe el marcador del PostgreSQL temporal de Vita.' }
$vitaCluster = [IO.Path]::GetFullPath((Get-Content -LiteralPath $vitaMarker -Raw).Trim())
$vitaTempPrefix = [IO.Path]::GetFullPath((Join-Path $env:TEMP 'vita-back-pg-'))
if (-not $vitaCluster.StartsWith($vitaTempPrefix, [StringComparison]::OrdinalIgnoreCase))
{ throw 'El marcador no pertenece al cluster temporal permitido.' }
$vitaExpectedData = [IO.Path]::GetFullPath((Join-Path $vitaCluster 'data'))
$vitaActualData = & psql -h 127.0.0.1 -p 55437 -U vita_test -d postgres -X -At -v ON_ERROR_STOP=1 -c 'SHOW data_directory'
if ($LASTEXITCODE -ne 0 -or [IO.Path]::GetFullPath(($vitaActualData -join '').Trim()) -ne $vitaExpectedData)
{ throw 'El servidor local no es el cluster temporal de Vita; no se crea ni elimina ninguna BD.' }
if (-not $ProvisioningOnly -and -not (Test-Path -LiteralPath (Join-Path $AiWorktree 'tests/test_vita_postgres.py')))
{ throw 'No existe el test PostgreSQL de Vita en el worktree AI indicado.' }

$vitaDatabase = 'vita_test_ai_' + [Guid]::NewGuid().ToString('N')
if ($vitaDatabase -notmatch '^vita_test_ai_[0-9a-f]{32}$') { throw 'Nombre de BD temporal no permitido.' }
$vitaPriorUrl = [Environment]::GetEnvironmentVariable('VITA_TEST_DATABASE_URL')
$vitaCreated = $false
try
{
    & psql -h 127.0.0.1 -p 55437 -U vita_test -d postgres -X -v ON_ERROR_STOP=1 -c ('CREATE DATABASE "' + $vitaDatabase + '" TEMPLATE template0') | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'No se pudo crear la BD temporal de Vita.' }
    $vitaCreated = $true
    # Solo se configura el proceso actual. Nunca imprimir esta variable ni la URI.
    $vitaUrl = [UriBuilder]::new()
    $vitaUrl.Scheme = 'postgresql+psycopg'; $vitaUrl.Host = '127.0.0.1'; $vitaUrl.Port = 55437
    $vitaUrl.UserName = 'vita_test'; $vitaUrl.Path = $vitaDatabase
    [Environment]::SetEnvironmentVariable('VITA_TEST_DATABASE_URL', $vitaUrl.Uri.AbsoluteUri)
    if ($ProvisioningOnly)
    {
        $vitaProbe = & psql -h 127.0.0.1 -p 55437 -U vita_test -d $vitaDatabase -X -At -c 'SELECT 1'
        if ($LASTEXITCODE -ne 0 -or $vitaProbe -ne '1') { throw 'Falló la sonda de la BD temporal.' }
        Write-Output 'Aprovisionamiento aislado verificado; URI oculta.'
    }
    else
    {
        $vitaTestLog = Join-Path $env:TEMP ('vita-ai-postgres-' + [Guid]::NewGuid().ToString('N') + '.log')
        Push-Location -LiteralPath $AiWorktree
        try
        {
            & uv run --no-sync pytest -q tests/test_vita_postgres.py *> $vitaTestLog
            $vitaTestExit = $LASTEXITCODE
        }
        finally { Pop-Location }
        if ($vitaTestExit -ne 0) { throw ('Falló la prueba AI; revisar log local protegido: ' + $vitaTestLog) }
        Get-Content -LiteralPath $vitaTestLog | Where-Object { $_ -match '^\d+ passed in ' }
        Write-Output 'Prueba AI completada contra una BD única descartable; URI oculta.'
    }
}
finally
{
    [Environment]::SetEnvironmentVariable('VITA_TEST_DATABASE_URL', $vitaPriorUrl)
    if ($vitaCreated)
    {
        # Se elimina exclusivamente el nombre generado en esta ejecución, en el cluster verificado.
        & psql -h 127.0.0.1 -p 55437 -U vita_test -d postgres -X -v ON_ERROR_STOP=1 -c ('DROP DATABASE IF EXISTS "' + $vitaDatabase + '" WITH (FORCE)') | Out-Null
        if ($LASTEXITCODE -ne 0) { Write-Warning ('No se pudo limpiar la BD temporal: ' + $vitaDatabase) }
        else { Write-Output 'BD temporal eliminada; cluster de pruebas permanece disponible.' }
    }
}
