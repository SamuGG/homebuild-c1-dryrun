#!/usr/bin/env bash
# Mints the Bruno collection's tokens from the local test issuer (dotnet user-jwts), as NAME=VALUE lines for
# $GITHUB_ENV or `export`. Never commit or print the output. Usage: api-tests/tokens.sh <client-id>
set -euo pipefail
client=${1:-homebuild-local}
project="$(dirname "$0")/../src/HomeBuild.Api"
mint() { dotnet user-jwts create --project "$project" --name "$1" --output token "${@:2}"; }

access=$(mint 11111111-1111-1111-1111-111111111111 --claim token_use=access --claim client_id="$client" --claim cognito:groups=buyer)
buyer2=$(mint 22222222-2222-2222-2222-222222222222 --claim token_use=access --claim client_id="$client" --claim cognito:groups=buyer)
# An ID token: token_use=id and no client_id (Cognito puts the client in aud instead).
id=$(mint 11111111-1111-1111-1111-111111111111 --claim token_use=id --claim cognito:groups=buyer)
# A role escalation: buyer 1's valid token with the payload rewritten to the admin group, original signature kept.
forged=$(python3 - "$access" <<'PY'
import base64, json, sys
header, payload, signature = sys.argv[1].split('.')
claims = json.loads(base64.urlsafe_b64decode(payload + '=' * (-len(payload) % 4)))
claims['cognito:groups'] = ['admin']
forged = base64.urlsafe_b64encode(json.dumps(claims, separators=(',', ':')).encode()).rstrip(b'=').decode()
print(f'{header}.{forged}.{signature}')
PY
)
printf 'BRUNO_ACCESS_TOKEN=%s\nBRUNO_BUYER2_TOKEN=%s\nBRUNO_ID_TOKEN=%s\nBRUNO_FORGED_ADMIN_TOKEN=%s\n' "$access" "$buyer2" "$id" "$forged"
