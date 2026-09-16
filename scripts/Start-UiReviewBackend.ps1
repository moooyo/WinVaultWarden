[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

# Provision only the dedicated loopback review instance; do not modify other services.
$remoteScript = @'
set -eu
if docker container inspect winvaultwarden-ui-review >/dev/null 2>&1; then
    docker start winvaultwarden-ui-review >/dev/null
else
    if ss -ltn | grep -q '127.0.0.1:18083 '; then
        echo 'Port 18083 is already occupied. Refusing to alter the listener.' >&2
        exit 1
    fi
    docker volume create winvaultwarden_ui_review_data >/dev/null
    docker run -d --name winvaultwarden-ui-review --restart unless-stopped \
        -p 127.0.0.1:18083:80 \
        -v winvaultwarden_ui_review_data:/data \
        -e DOMAIN=http://127.0.0.1:18083 \
        -e SIGNUPS_ALLOWED=true \
        -e INVITATIONS_ALLOWED=true \
        -e EMERGENCY_ACCESS_ALLOWED=true \
        -e WEBSOCKET_ENABLED=true \
        -e LOGIN_RATELIMIT_SECONDS=1 \
        -e LOGIN_RATELIMIT_MAX_BURST=200 \
        ghcr.io/dani-garcia/vaultwarden@sha256:094b5689ed81549bd293418395c7cf495ae9d960fc2d4928cef2083ef913d912 >/dev/null
fi
docker ps --filter name=winvaultwarden-ui-review --format '{{.Names}} {{.Status}} {{.Ports}}'
'@

$remoteScript.Replace("`r", '') | ssh test-env sh -s
if ($LASTEXITCODE -ne 0) {
    throw 'The isolated review backend could not be started.'
}

Write-Host 'Open the local tunnel with:'
Write-Host 'ssh -N -o ExitOnForwardFailure=yes -L 127.0.0.1:18083:127.0.0.1:18083 test-env'
