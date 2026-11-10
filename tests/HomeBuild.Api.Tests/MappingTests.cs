using HomeBuild.Api.Modules.Catalogue;
using HomeBuild.Api.Modules.Sales;
using HomeBuild.Domain;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace HomeBuild.Api.Tests;

public sealed class MappingTests(PostgresFixture database)
{
    private static readonly DateTimeOffset Now = new(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Unit_round_trips_with_money_and_specification()
    {
        var cs = await database.CreateDatabaseAsync();
        var developmentId = DevelopmentId.New();
        var unit = new Unit(UnitId.New(), developmentId, "P42", new Money(412_500.50m, "GBP"),
            new UnitSpecification(3, 2, true, ["oak floors", "quartz worktops"]));
        await using (var db = CatalogueModule.CreateDbContext(cs))
        {
            db.Developments.Add(new Development(developmentId, "Round Trip", "York", "fixed"));
            db.Units.Add(unit);
            await db.SaveChangesAsync(Ct);
        }

        await using var read = CatalogueModule.CreateDbContext(cs);
        var loaded = await read.Units.AsNoTracking().SingleAsync(u => u.Id == unit.Id, Ct);

        Assert.Equal(unit.DevelopmentId, loaded.DevelopmentId);
        Assert.Equal("P42", loaded.PlotNumber);
        Assert.Equal(new Money(412_500.50m, "GBP"), loaded.Price);
        Assert.Equal(3, loaded.Specification.Bedrooms);
        Assert.Equal(2, loaded.Specification.Bathrooms);
        Assert.True(loaded.Specification.Balcony);
        Assert.Equal(["oak floors", "quartz worktops"], loaded.Specification.Finishes);
    }

    [Fact]
    public async Task Reservation_round_trips_with_state()
    {
        var cs = await database.CreateDatabaseAsync();
        var unitId = new UnitId(Guid.Parse(EndpointTests.RiversidePlot1));
        var policy = new ReservationPolicy(TimeSpan.FromDays(7), new Money(1000m, "GBP"));
        Reservation reservation;
        await using (var db = SalesModule.CreateDbContext(cs))
        {
            var unit = await db.Units.SingleAsync(u => u.Id == unitId, Ct);
            reservation = Assert.IsType<Reserved>(unit.Reserve(BuyerId.New(), new Money(5000m, "GBP"), policy, Now).Value).Reservation;
            db.Reservations.Add(reservation);
            await db.SaveChangesAsync(Ct);
        }

        await using (var db = SalesModule.CreateDbContext(cs))
        {
            var held = await db.Reservations.SingleAsync(r => r.Id == reservation.Id, Ct);
            Assert.Equal(new Held(Now.AddDays(7)), held.State);
            Assert.Equal(reservation.BuyerId, held.BuyerId);
            Assert.Equal(new Money(5000m, "GBP"), held.Deposit);
            Assert.Equal(Now, held.CreatedAt);
            Assert.Equal(UnitStatus.Reserved, (await db.Units.SingleAsync(u => u.Id == unitId, Ct)).Status);
            Assert.IsType<Transitioned>(held.Cancel(Now.AddHours(1)).Value);
            await db.SaveChangesAsync(Ct);
        }

        await using var read = SalesModule.CreateDbContext(cs);
        var cancelled = await read.Reservations.AsNoTracking().SingleAsync(r => r.Id == reservation.Id, Ct);
        Assert.Equal(new Cancelled(Now.AddHours(1)), cancelled.State);
        Assert.Equal("cancelled", await read.Reservations.Where(r => r.Id == reservation.Id)
            .Select(r => EF.Property<string>(r, "Status")).SingleAsync(Ct));
    }

    [Fact]
    public void Domain_assembly_does_not_reference_EF_Core()
    {
        var references = typeof(Unit).Assembly.GetReferencedAssemblies().Select(a => a.Name ?? "").ToList();

        Assert.DoesNotContain(references, r => r.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal));
        Assert.DoesNotContain(references, r => r.StartsWith("Npgsql", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Each_module_uses_its_own_schema()
    {
        await using (var catalogue = CatalogueModule.CreateDbContext("Host=unused"))
        await using (var sales = SalesModule.CreateDbContext("Host=unused"))
        {
            Assert.All(catalogue.Model.GetEntityTypes(), e => Assert.Equal("catalogue", e.GetSchema()));
            Assert.All(sales.Model.GetEntityTypes(), e => Assert.Equal("sales", e.GetSchema()));
        }

        var cs = await database.CreateDatabaseAsync();
        await using var connection = new NpgsqlConnection(cs);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(
            "SELECT table_schema || '.' || table_name FROM information_schema.tables " +
            "WHERE table_schema IN ('catalogue', 'sales') ORDER BY 1", connection);
        var tables = new List<string>();
        await using (var reader = await command.ExecuteReaderAsync(Ct))
        {
            while (await reader.ReadAsync(Ct))
            {
                tables.Add(reader.GetString(0));
            }
        }

        Assert.Equal(
            ["catalogue.__ef_migrations_history", "catalogue.developments", "catalogue.unit_prices", "catalogue.units",
             "sales.__ef_migrations_history", "sales.reservations", "sales.units"],
            tables);
    }
}
