#!/usr/bin/env bash
# Creates HomeBuild's test users: one per role per tenant (tenant-a, tenant-b), example.com addresses,
# no invitation email, a generated permanent password, and membership of the role's group.
# Passwords are written only to the file given as $2 (keep it outside the repository); never printed.
# With $3, each user's tenant membership (C4 Lab 5) is also upserted into that database of the local compose
# PostgreSQL, as the table owner: tenant-a is the demo tenant that owns C2's fixed data, tenant-b the second tenant.
# Usage: scripts/cognito-users.sh <user-pool-id> <password-file> [<compose-database-name>]
set -euo pipefail
pool=$1
out=$2
memberships_db=${3:-}
region=${AWS_REGION:-eu-west-2}
roles=(admin sales-agent site-manager finance aftercare buyer)
tenant_id() { case $1 in tenant-a) echo 0198a000-0000-7000-8000-00000000000a ;; tenant-b) echo 0198a000-0000-7000-8000-00000000000b ;; esac; }
umask 077
: > "$out"
for tenant in tenant-a tenant-b; do
  for role in "${roles[@]}"; do
    email="${role}.${tenant}@example.com"
    if ! aws cognito-idp admin-get-user --region "$region" --user-pool-id "$pool" --username "$email" >/dev/null 2>&1; then
      aws cognito-idp admin-create-user --region "$region" --user-pool-id "$pool" --username "$email" \
        --user-attributes Name=email,Value="$email" Name=email_verified,Value=true \
        --message-action SUPPRESS >/dev/null
    fi
    password="Hb-$(openssl rand -base64 24 | tr -d '/+=' | cut -c1-20)-9a"
    aws cognito-idp admin-set-user-password --region "$region" --user-pool-id "$pool" --username "$email" \
      --password "$password" --permanent
    aws cognito-idp admin-add-user-to-group --region "$region" --user-pool-id "$pool" --username "$email" --group-name "$role"
    sub=$(aws cognito-idp admin-get-user --region "$region" --user-pool-id "$pool" --username "$email" \
      --query "UserAttributes[?Name=='sub'].Value | [0]" --output text)
    printf '%s\t%s\t%s\t%s\n' "$email" "$tenant" "$sub" "$password" >> "$out"
    if [ -n "$memberships_db" ]; then
      docker compose exec -T postgres psql -q -v ON_ERROR_STOP=1 -U homebuild -d "$memberships_db" -c \
        "INSERT INTO accounts.tenant_memberships (subject, tenant_id, roles) VALUES ('$sub', '$(tenant_id "$tenant")', ARRAY['$role'])
         ON CONFLICT (subject) DO UPDATE SET tenant_id = EXCLUDED.tenant_id, roles = EXCLUDED.roles;"
    fi
    echo "ok  $email ($role, $tenant${memberships_db:+, membership})"
  done
done
