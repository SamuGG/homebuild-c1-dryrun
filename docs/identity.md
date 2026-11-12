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

## Web sign-in (backend-for-frontend)

`HomeBuild.Web` signs users in through the confidential client `homebuild-web`. It uses authorisation code with PKCE and a client secret, with callback `http://localhost:5266/signin-oidc` and sign-out return `http://localhost:5266/`. Code: `src/HomeBuild.Web/Auth/`.

- **Where the tokens live.** The OIDC handler does the code exchange on the web server. `SaveTokens` is off: the access and refresh tokens go to `TokenStore` (server-side, a distributed cache), keyed by a session ID. The browser receives only the `homebuild.session` cookie (`HttpOnly`, `Secure`, `SameSite=Lax`), which holds the encrypted claims and that session ID.
- **Calling the API.** `AccessTokenHandler` (a `DelegatingHandler` on `HomeApiClient`) adds the user's access token. `UserTokenService` refreshes it with the refresh token when it has expired, or will within a minute. Concurrent callers for one session share a single refresh.
- **Interactive components** never read `HttpContext`. The handler finds the circuit's user through `CircuitServicesAccessor`.
- **Sign-out** is a `POST /account/logout` with an antiforgery token. It deletes the stored tokens, clears the cookie, and redirects to Cognito's `/logout?client_id=…&logout_uri=…`, which ends the Cognito session too. Cognito's discovery document has no `end_session_endpoint`, so this URL is built by hand.
- **Antiforgery.** `app.UseAntiforgery()` is called on purpose. .NET 11 also rejects cross-site form posts by `Sec-Fetch-Site` and `Origin` by default, and its template no longer calls `UseAntiforgery`; without the call, `<AntiforgeryToken />` renders nothing.
- **The client secret** is in SSM (`/homebuild/web/Cognito/ClientSecret`, SecureString) and in local user-secrets. It is never in the repository.

Running it locally against the dev pool:

```bash
# Web app: user-secrets (once)
dotnet user-secrets set --project src/HomeBuild.Web "Authentication:Cognito:Authority" "https://cognito-idp.eu-west-2.amazonaws.com/<user-pool-id>"
dotnet user-secrets set --project src/HomeBuild.Web "Authentication:Cognito:ClientId" "<web-client-id>"
# the secret, piped so it is never printed or passed as an argument
aws cognito-idp describe-user-pool-client --user-pool-id <user-pool-id> --client-id <web-client-id> \
  --query UserPoolClient.ClientSecret --output text \
  | python3 -c "import json,sys; print(json.dumps({'Authentication:Cognito:ClientSecret': sys.stdin.read().strip()}))" \
  | dotnet user-secrets set --project src/HomeBuild.Web

# API: trust the pool and the web client (Development otherwise trusts only dotnet user-jwts)
Authentication__Schemes__Bearer__Authority=https://cognito-idp.eu-west-2.amazonaws.com/<user-pool-id> \
Authentication__Schemes__Bearer__ValidIssuer=https://cognito-idp.eu-west-2.amazonaws.com/<user-pool-id> \
Tokens__AllowedClientIds__1=<web-client-id> dotnet run --project src/HomeBuild.Api --launch-profile http
```

The Playwright tests in `tests/HomeBuild.Web.Tests/EndToEnd/` sign in through the real managed login. They are opt-in: `HOMEBUILD_COGNITO_TESTS=1 HOMEBUILD_TEST_USERS=<password-file> dotnet test tests/HomeBuild.Web.Tests`. The browsers are installed once with Playwright's `install chromium`.

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
