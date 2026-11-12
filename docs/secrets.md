# Secrets

Every secret has one home per environment; everything else holds a reference.

| Secret | Development | CI | AWS (staging, production) | Owner | Rotation |
| --- | --- | --- | --- | --- | --- |
| Database connection string (app role password) | `dotnet user-secrets` (`ConnectionStrings:HomeBuild`) | workflow `env`, throwaway container | SSM `SecureString` `/homebuild/{env}/ConnectionStrings/HomeBuild` (C12) | platform | on each environment rebuild; C12 moves to rotation |
| Test-issuer signing key (`dotnet user-jwts`) | user-secrets (written by `dotnet user-jwts`) | minted per run, masked | not used: AWS trusts Cognito's JWKS | each developer | `dotnet user-jwts key --reset` |
| Payment provider secret key (C8) | user-secrets (`Payments:SecretKey`) | not needed | SSM `SecureString` `/homebuild/{env}/Payments/SecretKey` | finance + platform | per provider policy; readers accept old and new during the switch |

Rules:

- `appsettings*.json` hold no secrets: `No_secrets_in_appsettings` fails the build if one appears.
- Options properties that hold secrets carry `[Secret]` and are logged as `***` (`SecretRedaction.Describe`).
- In AWS the API adds the SSM provider for `/homebuild/{env}/` last, so SSM overrides appsettings. Standard
  parameters are free; `SecureString` uses the AWS-managed `aws/ssm` key.
- Never paste a secret or token into a prompt, an issue or a log; use a placeholder.
- A secret that reached git history, a log or a prompt is rotated, not deleted.
