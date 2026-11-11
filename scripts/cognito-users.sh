#!/usr/bin/env bash
# Creates HomeBuild's test users: one per role per tenant (tenant-a, tenant-b), example.com addresses,
# no invitation email, a generated permanent password, and membership of the role's group.
# Passwords are written only to the file given as $2 (keep it outside the repository); never printed.
# Usage: scripts/cognito-users.sh <user-pool-id> <password-file>
set -euo pipefail
pool=$1
out=$2
region=${AWS_REGION:-eu-west-2}
roles=(admin sales-agent site-manager finance aftercare buyer)
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
    echo "ok  $email ($role, $tenant)"
  done
done
