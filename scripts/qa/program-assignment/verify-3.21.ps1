param(
    [ValidateSet('Audit', 'Postgres')][string]$Mode = 'Audit',
    [string]$ConfigPath = '../../coppAddresdBack/src/CoppAddresd.Api/appsettings.Development.json',
    [string]$Evidence = 'docs/modules/program-progress/evidence/3.21'
)
$ErrorActionPreference = 'Stop'
# Resolver credenciales locales solo en memoria; nunca imprimir la configuración.
$config = Get-Content -Raw -Encoding UTF8 -LiteralPath $ConfigPath | ConvertFrom-Json
$connection = $config.ConnectionStrings.DefaultConnection
if ($connection -notmatch '(?i)(^|;)\s*Host\s*=\s*(localhost|127\.0\.0\.1)\s*(;|$)') {
    throw 'La verificación requiere PostgreSQL local.'
}
$previous = $env:COP_TEST_DB_CONNECTION
try {
    $env:COP_TEST_DB_CONNECTION = $connection
    # Windows PowerShell convierte stderr nativo en ErrorRecord: conservar el log y exit code.
    $ErrorActionPreference = 'Continue'
    if ($Mode -eq 'Audit') {
        $head = git rev-parse HEAD
        dotnet scripts/ProgramPhaseVerification/bin/Verification321/ProgramPhaseVerification.dll "$Evidence/database-audit.json" $head *> "$Evidence/database-audit.log"
    } else {
        # Cada prueba crea y elimina su propia BD aislada; no migra la compartida.
        dotnet test tests/CoppAddresd.IntegrationTests --no-build -p:OutputPath=bin/Verification321/ --results-directory $Evidence --logger 'trx;LogFileName=postgres.trx' -v quiet *> "$Evidence/postgres.log"
    }
    $result = $LASTEXITCODE
} finally {
    $env:COP_TEST_DB_CONNECTION = $previous
}
exit $result
