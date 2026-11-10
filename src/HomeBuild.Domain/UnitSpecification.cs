namespace HomeBuild.Domain;

/// <summary>What a unit is: rooms and finishes. Read whole, rarely queried inside.</summary>
public sealed record UnitSpecification
{
    public UnitSpecification(int bedrooms, int bathrooms, bool balcony, IReadOnlyList<string> finishes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bedrooms);
        ArgumentOutOfRangeException.ThrowIfNegative(bathrooms);
        ArgumentNullException.ThrowIfNull(finishes);
        Bedrooms = bedrooms;
        Bathrooms = bathrooms;
        Balcony = balcony;
        Finishes = finishes;
    }

    public static UnitSpecification Unspecified { get; } = new(0, 0, false, []);

    public int Bedrooms { get; }

    public int Bathrooms { get; }

    public bool Balcony { get; }

    public IReadOnlyList<string> Finishes { get; }
}
