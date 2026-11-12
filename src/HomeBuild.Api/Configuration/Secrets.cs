using System.Globalization;
using System.Reflection;

namespace HomeBuild.Api.Configuration;

/// <summary>Marks an options property that holds a secret: it is never written to logs (C1 Lab 6's rules).</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class SecretAttribute : Attribute;

/// <summary>Payment provider settings. C8 fills them; the secret key comes from user-secrets locally and SSM in AWS.</summary>
public sealed class PaymentsOptions
{
    public const string Section = "Payments";

    [Secret]
    public string? SecretKey { get; set; }

    public string? PublishableKey { get; set; }
}

public static class SecretRedaction
{
    public const string Redacted = "***";

    /// <summary>"Name=value, …" for logs, with every <see cref="SecretAttribute"/> property shown as *** (or "(not set)").</summary>
    public static string Describe(object options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return string.Join(", ", options.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(p =>
        {
            var value = p.GetValue(options);
            var shown = value is null ? "(not set)"
                : p.GetCustomAttribute<SecretAttribute>() is not null ? Redacted
                : Convert.ToString(value, CultureInfo.InvariantCulture);
            return $"{p.Name}={shown}";
        }));
    }
}
