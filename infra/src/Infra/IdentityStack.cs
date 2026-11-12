using Amazon.CDK;
using Amazon.CDK.AWS.Cognito;
using Constructs;

namespace Infra;

/// <summary>
/// HomeBuild's identity provider: a Cognito user pool with a group per role, a public app client for dev tools
/// (Bruno, authorisation code + PKCE) and managed login. Dev only: the pool is destroyed with the stack.
/// </summary>
#pragma warning disable CA1711 // CDK names stacks "...Stack" by convention
public sealed class IdentityStack : Stack
#pragma warning restore CA1711
{
    public static readonly string[] Roles = ["admin", "sales-agent", "site-manager", "finance", "aftercare", "buyer"];

    public IdentityStack(Construct scope, string id, IStackProps props, string domainPrefix)
        : base(scope, id, props)
    {
        var pool = new UserPool(this, "UserPool", new UserPoolProps
        {
            UserPoolName = "homebuild-dev",
            // Essentials: managed login needs Essentials or Plus (Lite has only the classic hosted UI).
            // Lite and Essentials are both free up to 10,000 monthly active users.
            FeaturePlan = FeaturePlan.ESSENTIALS,
            SignInAliases = new SignInAliases { Email = true },
            SignInCaseSensitive = false,
            SelfSignUpEnabled = false, // users come from scripts/cognito-users.sh, never the console
            Mfa = Mfa.OPTIONAL,
            MfaSecondFactor = new MfaSecondFactor { Otp = true, Sms = false },
            PasswordPolicy = new PasswordPolicy
            {
                MinLength = 14,
                RequireLowercase = true,
                RequireUppercase = true,
                RequireDigits = true,
                RequireSymbols = true,
                TempPasswordValidity = Duration.Days(3),
            },
            AccountRecovery = AccountRecovery.EMAIL_ONLY,
            // Dev pool: deleted with the stack. Production keeps users: RETAIN plus deletion protection (see docs/identity.md).
            RemovalPolicy = RemovalPolicy.DESTROY,
            DeletionProtection = false,
        });

        foreach (var role in Roles)
        {
            _ = new UserPoolGroup(this, "Group-" + role, new UserPoolGroupProps
            {
                UserPool = pool,
                GroupName = role,
                Description = "HomeBuild role " + role,
            });
        }

        var devTools = pool.AddClient("DevToolsClient", new UserPoolClientOptions
        {
            UserPoolClientName = "homebuild-dev-tools",
            GenerateSecret = false, // public client: Bruno cannot keep a secret, so it uses PKCE
            AuthFlows = new AuthFlow { UserSrp = true },
            OAuth = new OAuthSettings
            {
                Flows = new OAuthFlows { AuthorizationCodeGrant = true },
                Scopes = [OAuthScope.OPENID, OAuthScope.EMAIL, OAuthScope.PROFILE],
                CallbackUrls = ["http://localhost:8080/callback"], // Bruno's OAuth 2.0 callback
                LogoutUrls = ["http://localhost:8080/logout"],
            },
            SupportedIdentityProviders = [UserPoolClientIdentityProvider.COGNITO],
            AccessTokenValidity = Duration.Minutes(60),
            IdTokenValidity = Duration.Minutes(60),
            RefreshTokenValidity = Duration.Days(1),
            PreventUserExistenceErrors = true,
        });

        var domain = pool.AddDomain("Domain", new UserPoolDomainOptions
        {
            CognitoDomain = new CognitoDomainOptions { DomainPrefix = domainPrefix },
            ManagedLoginVersion = ManagedLoginVersion.NEWER_MANAGED_LOGIN,
        });

        // Managed login shows nothing for a client until it has a style; Cognito's default style is enough here.
        _ = new CfnManagedLoginBranding(this, "DevToolsBranding", new CfnManagedLoginBrandingProps
        {
            UserPoolId = pool.UserPoolId,
            ClientId = devTools.UserPoolClientId,
            UseCognitoProvidedValues = true,
        });

        _ = new CfnOutput(this, "UserPoolId", new CfnOutputProps { Value = pool.UserPoolId });
        _ = new CfnOutput(this, "DevToolsClientId", new CfnOutputProps { Value = devTools.UserPoolClientId });
        _ = new CfnOutput(this, "Issuer", new CfnOutputProps { Value = pool.UserPoolProviderUrl });
        _ = new CfnOutput(this, "ManagedLoginDomain", new CfnOutputProps { Value = domain.BaseUrl() });
    }
}
