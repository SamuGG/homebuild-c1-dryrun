using System.Globalization;
using System.Security.Cryptography;
using HomeBuild.Domain;
using Microsoft.AspNetCore.DataProtection;

namespace HomeBuild.Api.Modules.Catalogue;

/// <summary>
/// The keyset position (price, unit id) as an opaque, tamper-evident string. Data Protection encrypts and
/// authenticates it, so a client can neither read nor change it. Several API instances need a shared key ring (C12).
/// </summary>
internal sealed class UnitsCursor(IDataProtectionProvider provider)
{
    private readonly IDataProtector _protector = provider.CreateProtector("HomeBuild.Catalogue.UnitsCursor.v1");

    public string Encode(decimal price, UnitId unitId) =>
        _protector.Protect(string.Create(CultureInfo.InvariantCulture, $"{price}|{unitId.Value}"));

    public bool TryDecode(string cursor, out decimal price, out UnitId unitId)
    {
        price = default;
        unitId = default;
        try
        {
            var parts = _protector.Unprotect(cursor).Split('|');
            if (parts.Length == 2
                && decimal.TryParse(parts[0], NumberStyles.Number, CultureInfo.InvariantCulture, out price)
                && UnitId.TryParse(parts[1], out unitId))
            {
                return true;
            }
        }
        catch (CryptographicException)
        {
            // Tampered, truncated or from another key ring.
        }

        return false;
    }
}
