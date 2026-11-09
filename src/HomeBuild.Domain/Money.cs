namespace HomeBuild.Domain;

public sealed record Money
{
    public Money(decimal amount, string currency)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(amount);
        ArgumentNullException.ThrowIfNull(currency);
        if (currency.Length != 3 || !currency.All(char.IsAsciiLetterUpper))
        {
            throw new ArgumentException("Currency must be a three-letter code.", nameof(currency));
        }

        Amount = amount;
        Currency = currency;
    }

    public decimal Amount { get; }

    public string Currency { get; }

    public static Money operator +(Money left, Money right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        if (left.Currency != right.Currency)
        {
            throw new InvalidOperationException($"Cannot add {left.Currency} to {right.Currency}.");
        }

        return new Money(left.Amount + right.Amount, left.Currency);
    }

    public static Money Add(Money left, Money right) => left + right;
}
