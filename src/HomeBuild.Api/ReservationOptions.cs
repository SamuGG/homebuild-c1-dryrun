using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Options;

namespace HomeBuild.Api;

public sealed class ReservationOptions
{
    public const string Section = "Reservation";

    [Range(typeof(TimeSpan), "01:00:00", "14.00:00:00")]
    public TimeSpan HoldPeriod { get; set; }

    [Range(typeof(decimal), "0.01", "1000000", ParseLimitsInInvariantCulture = true)]
    public decimal MinimumDeposit { get; set; }

    [Required, RegularExpression("^[A-Z]{3}$")]
    public string Currency { get; set; } = "";
}

[OptionsValidator]
public sealed partial class ValidateReservationOptions
    : IValidateOptions<ReservationOptions>;
