namespace HomeBuild.Web.Auth;

/// <summary>The web app's Cognito client. The secret comes from user-secrets locally and SSM in AWS.</summary>
public sealed class CognitoOptions
{
    public const string Section = "Authentication:Cognito";

    /// <summary>The user pool's issuer, for example https://cognito-idp.eu-west-2.amazonaws.com/eu-west-2_abc.</summary>
    public string Authority { get; set; } = "";

    public string ClientId { get; set; } = "";

    public string ClientSecret { get; set; } = "";
}
