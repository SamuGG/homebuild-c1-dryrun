// Writes HomeBuild's deterministic volume data set for Labs 4 to 6:
// 20 tenants (10 developments each), 200 developments, 50,000 units (250 each), 150,000 price-history rows, 5,000 reservations.
// Usage: dotnet run --project tools/HomeBuild.Seed -- "<connection string>"
// Run after the migrations, as the table owner (it writes every tenant, so it must not be subject to RLS);
// it refuses to run twice on the same database.
using System.Diagnostics;
using System.Globalization;
using Npgsql;
using NpgsqlTypes;

const int Developments = 200;
const int UnitsPerDevelopment = 250;
const int PricesPerUnit = 3;
const int Reservations = 5_000;
const int Buyers = 1_000;
const int Tenants = 20;

var connectionString = args.Length > 0 ? args[0]
    : Environment.GetEnvironmentVariable("ConnectionStrings__HomeBuild")
    ?? "Host=localhost;Port=5432;Database=homebuild;Username=homebuild;Password=homebuild-dev";
var random = new Random(2026); // fixed seed: the same data on every machine
var now = new DateTimeOffset(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);
var clock = Stopwatch.StartNew();

await using var dataSource = NpgsqlDataSource.Create(connectionString);
await using (var check = dataSource.CreateCommand("SELECT count(*) FROM catalogue.developments"))
{
    if ((long)(await check.ExecuteScalarAsync())! >= Developments)
    {
        Console.WriteLine("Already seeded; nothing to do.");
        return;
    }
}

string Describe(int bedrooms) =>
    $"{bedrooms}-bedroom {Pick("home", "apartment", "townhouse", "cottage")} with {Pick("a south-facing balcony", "a private garden", "a roof terrace", "a courtyard")}, {Pick("views over the river", "views across the hills", "a quiet cul-de-sac setting", "walking distance to the station")} and {Pick("oak floors", "quartz worktops", "underfloor heating", "a fitted kitchen")}.";

string Pick(params string[] options) => options[random.Next(options.Length)];

Guid NextGuid()
{
    Span<byte> bytes = stackalloc byte[16];
    random.NextBytes(bytes);
    bytes[7] = (byte)((bytes[7] & 0x0F) | 0x40); // version 4
    bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
    return new Guid(bytes);
}

var tenantIds = Enumerable.Range(0, Tenants).Select(_ => NextGuid()).ToArray();
var developmentIds = Enumerable.Range(0, Developments).Select(_ => NextGuid()).ToArray();
var tenantOf = developmentIds.Select((d, i) => (d, tenantIds[i % Tenants])).ToDictionary(x => x.d, x => x.Item2);
var units = new List<(Guid Id, Guid Development, string Plot, decimal Price)>(Developments * UnitsPerDevelopment);
foreach (var development in developmentIds)
{
    for (var plot = 1; plot <= UnitsPerDevelopment; plot++)
    {
        units.Add((NextGuid(), development, $"P{plot}", 180_000m + (random.Next(0, 4_200) * 100m)));
    }
}

var reservedUnits = units.OrderBy(_ => random.Next()).Take(Reservations).ToArray();
var buyers = Enumerable.Range(0, Buyers).Select(_ => NextGuid()).ToArray();

await using var connection = await dataSource.OpenConnectionAsync();
await using var transaction = await connection.BeginTransactionAsync();

await using (var copy = await connection.BeginBinaryImportAsync(
    "COPY catalogue.developments (id, tenant_id, name, town, deposit_strategy) FROM STDIN (FORMAT BINARY)"))
{
    for (var i = 0; i < Developments; i++)
    {
        await copy.WriteRowAsync(default, developmentIds[i], tenantOf[developmentIds[i]], $"Development {i + 1:000}", $"Town {i % 37:00}", i % 2 == 0 ? "fixed" : "percentage");
    }

    await copy.CompleteAsync();
}

await using (var copy = await connection.BeginBinaryImportAsync(
    "COPY catalogue.units (id, tenant_id, development_id, plot_number, price_amount, price_currency, specification, name, description) FROM STDIN (FORMAT BINARY)"))
{
    foreach (var u in units)
    {
        var bedrooms = random.Next(1, 6);
        var spec = string.Create(CultureInfo.InvariantCulture,
            $"{{\"Bedrooms\": {bedrooms}, \"Bathrooms\": {Math.Max(1, bedrooms - 1)}, \"Balcony\": {(random.Next(10) == 0 ? "true" : "false")}, \"Finishes\": [\"oak floors\"]}}");
        await copy.StartRowAsync();
        await copy.WriteAsync(u.Id, NpgsqlDbType.Uuid);
        await copy.WriteAsync(tenantOf[u.Development], NpgsqlDbType.Uuid);
        await copy.WriteAsync(u.Development, NpgsqlDbType.Uuid);
        await copy.WriteAsync(u.Plot, NpgsqlDbType.Varchar);
        await copy.WriteAsync(u.Price, NpgsqlDbType.Numeric);
        await copy.WriteAsync("GBP", NpgsqlDbType.Char);
        await copy.WriteAsync(spec, NpgsqlDbType.Jsonb);
        await copy.WriteAsync($"{u.Plot}, {u.Development.ToString()[..8]}", NpgsqlDbType.Varchar);
        await copy.WriteAsync(Describe(bedrooms), NpgsqlDbType.Varchar);
    }

    await copy.CompleteAsync();
}

await using (var copy = await connection.BeginBinaryImportAsync(
    "COPY catalogue.unit_prices (id, tenant_id, unit_id, development_id, amount, currency, effective_from) FROM STDIN (FORMAT BINARY)"))
{
    foreach (var u in units)
    {
        for (var step = PricesPerUnit - 1; step >= 0; step--)
        {
            await copy.StartRowAsync();
            await copy.WriteAsync(NextGuid(), NpgsqlDbType.Uuid);
            await copy.WriteAsync(tenantOf[u.Development], NpgsqlDbType.Uuid);
            await copy.WriteAsync(u.Id, NpgsqlDbType.Uuid);
            await copy.WriteAsync(u.Development, NpgsqlDbType.Uuid);
            await copy.WriteAsync(u.Price - (step * 5_000m), NpgsqlDbType.Numeric);
            await copy.WriteAsync("GBP", NpgsqlDbType.Char);
            await copy.WriteAsync(now.AddDays(-90 * step).UtcDateTime, NpgsqlDbType.TimestampTz);
        }
    }

    await copy.CompleteAsync();
}

var reserved = reservedUnits.Select(u => u.Id).ToHashSet();
await using (var copy = await connection.BeginBinaryImportAsync(
    "COPY sales.units (id, tenant_id, development_id, plot_number, price_amount, price_currency, status) FROM STDIN (FORMAT BINARY)"))
{
    foreach (var u in units)
    {
        await copy.WriteRowAsync(default, u.Id, tenantOf[u.Development], u.Development, u.Plot, u.Price, "GBP", reserved.Contains(u.Id) ? "Reserved" : "Available");
    }

    await copy.CompleteAsync();
}

await using (var copy = await connection.BeginBinaryImportAsync(
    "COPY sales.reservations (id, tenant_id, unit_id, buyer_id, created_at, expires_at, state, deposit_amount, deposit_currency) FROM STDIN (FORMAT BINARY)"))
{
    foreach (var u in reservedUnits)
    {
        // Created over the last 7 days with a 7-day hold: some expire within the hour, some are already stale.
        var created = now.AddMinutes(-random.Next(0, 7 * 24 * 60));
        var expires = created.AddDays(7);
        await copy.StartRowAsync();
        await copy.WriteAsync(NextGuid(), NpgsqlDbType.Uuid);
        await copy.WriteAsync(tenantOf[u.Development], NpgsqlDbType.Uuid);
        await copy.WriteAsync(u.Id, NpgsqlDbType.Uuid);
        await copy.WriteAsync(buyers[random.Next(Buyers)], NpgsqlDbType.Uuid);
        await copy.WriteAsync(created.UtcDateTime, NpgsqlDbType.TimestampTz);
        await copy.WriteAsync(expires.UtcDateTime, NpgsqlDbType.TimestampTz);
        await copy.WriteAsync("Held|" + expires.ToString("O", CultureInfo.InvariantCulture), NpgsqlDbType.Varchar);
        await copy.WriteAsync(5_000m, NpgsqlDbType.Numeric);
        await copy.WriteAsync("GBP", NpgsqlDbType.Char);
    }

    await copy.CompleteAsync();
}

await transaction.CommitAsync();
await using (var analyze = dataSource.CreateCommand("ANALYZE"))
{
    await analyze.ExecuteNonQueryAsync();
}

Console.WriteLine($"Seeded {Developments} developments, {units.Count} units, {units.Count * PricesPerUnit} prices, {Reservations} reservations in {clock.Elapsed.TotalSeconds:0.0} s.");
