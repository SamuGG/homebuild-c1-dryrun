using HomeBuild.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace HomeBuild.Api.Data;

internal sealed class UnitIdConverter() : ValueConverter<UnitId, Guid>(id => id.Value, value => new UnitId(value));

internal sealed class DevelopmentIdConverter() : ValueConverter<DevelopmentId, Guid>(id => id.Value, value => new DevelopmentId(value));

internal sealed class ReservationIdConverter() : ValueConverter<ReservationId, Guid>(id => id.Value, value => new ReservationId(value));

internal sealed class BuyerIdConverter() : ValueConverter<BuyerId, Guid>(id => id.Value, value => new BuyerId(value));

/// <summary>Stores the closed <see cref="ReservationState"/> hierarchy as "Kind|timestamp" in one text column.</summary>
internal sealed class ReservationStateConverter() : ValueConverter<ReservationState, string>(
    state => ToText(state),
    text => FromText(text))
{
    private static string ToText(ReservationState state) => state switch
    {
        Held h => "Held|" + h.ExpiresAt.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
        Expired e => "Expired|" + e.At.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
        Confirmed c => "Confirmed|" + c.DepositPaidAt.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
        Cancelled c => "Cancelled|" + c.At.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
    };

    private static ReservationState FromText(string text)
    {
        var parts = text.Split('|');
        var at = DateTimeOffset.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind);
        return parts[0] switch
        {
            "Held" => new Held(at),
            "Expired" => new Expired(at),
            "Confirmed" => new Confirmed(at),
            "Cancelled" => new Cancelled(at),
            _ => throw new InvalidOperationException($"Unknown reservation state '{parts[0]}'."),
        };
    }
}

internal static class ModelConventions
{
    public static void AddStronglyTypedIds(this ModelConfigurationBuilder builder)
    {
        builder.Properties<UnitId>().HaveConversion<UnitIdConverter>();
        builder.Properties<DevelopmentId>().HaveConversion<DevelopmentIdConverter>();
        builder.Properties<ReservationId>().HaveConversion<ReservationIdConverter>();
        builder.Properties<BuyerId>().HaveConversion<BuyerIdConverter>();
    }
}
