# Identity (Cognito)

`infra/src/Infra/IdentityStack.cs` creates the dev user pool `homebuild-dev` in `eu-west-2`:
Essentials tier (managed login; free up to 10,000 monthly active users), email sign-in, optional TOTP MFA,
a 14-character password policy, a group per role (`admin`, `sales-agent`, `site-manager`, `finance`,
`aftercare`, `buyer`), the public app client `homebuild-dev-tools` (authorisation code + PKCE, callback
`http://localhost:8080/callback` for Bruno) and a managed-login domain.

Test users: `scripts/cognito-users.sh <user-pool-id> <password-file>` creates one user per role per tenant
(`<role>.tenant-a@example.com`, `<role>.tenant-b@example.com`) with no invitation email and a generated
password written only to the password file, which must stay outside the repository. With a third argument
(a database in the local compose PostgreSQL) it also writes each user's tenant membership: `tenant-a` users
belong to the demo tenant that owns C2's fixed data, `tenant-b` users to the second tenant.

## Removal policy

| | Dev (this stack) | Production |
| --- | --- | --- |
| `RemovalPolicy` | `DESTROY`: the pool and its users go with the stack | `RETAIN`: deleting the stack must never delete users |
| `DeletionProtection` | off, so `cdk destroy` works | on: even a deliberate delete needs a second step |
| Why | users are re-created by script in about two minutes | users, their passwords and MFA devices cannot be re-created by anyone but the users |

## Settings that cannot change after creation

Cognito does not allow these on an existing pool, so changing them means a new pool and every user
re-registering. Decide them before production:

- sign-in identifiers (`SignInAliases`: email vs username vs phone) and case sensitivity;
- required standard attributes, and custom attributes once added (they can be added, never removed);
- the domain prefix is unique per Region and is freed when the pool is deleted.

CloudFormation's resource schema for `AWS::Cognito::UserPool` marks no property as create-only, so
`cdk diff` shows these changes as ordinary updates, not replacements. Do not rely on the diff to warn you:
treat any change to the settings above as "new pool", and test it on a throwaway stack first.
