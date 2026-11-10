using HomeBuild.Api.Http;
using Microsoft.EntityFrameworkCore.Migrations;

namespace HomeBuild.Api.Data;

/// <summary>
/// Layer 2 of tenant isolation: PostgreSQL row-level security. The app connects as <see cref="AppRole"/>, which does not
/// own the tables, so the policies apply to it. Migrations run as the owner, outside RLS.
/// </summary>
internal static class RowLevelSecurity
{
    public const string AppRole = "homebuild_app";
    public const string Setting = "app.tenant_id";

    // NULLIF: after a transaction that set the value ends, current_setting returns '' (not NULL) on that connection.
    private const string CurrentTenant = "NULLIF(current_setting('app.tenant_id', true), '')::uuid";

    public static void EnableFor(MigrationBuilder migrationBuilder, string schema, params string[] tables)
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);
        // The role is created without a password; each environment gives it LOGIN and a password outside the repository.
        migrationBuilder.Sql($"""
            DO $$ BEGIN
              IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = '{AppRole}') THEN CREATE ROLE {AppRole} NOLOGIN; END IF;
            END $$;
            GRANT USAGE ON SCHEMA {schema} TO {AppRole};
            GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA {schema} TO {AppRole};
            REVOKE ALL ON {schema}.__ef_migrations_history FROM {AppRole};
            ALTER DEFAULT PRIVILEGES IN SCHEMA {schema} GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO {AppRole};
            """);
        foreach (var table in tables)
        {
            migrationBuilder.Sql($"""
                ALTER TABLE {schema}.{table} ENABLE ROW LEVEL SECURITY;
                CREATE POLICY tenant_isolation ON {schema}.{table}
                  USING (tenant_id = {CurrentTenant})
                  WITH CHECK (tenant_id = {CurrentTenant});
                """);
        }
    }

    public static void DisableFor(MigrationBuilder migrationBuilder, string schema, params string[] tables)
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);
        foreach (var table in tables)
        {
            migrationBuilder.Sql($"DROP POLICY IF EXISTS tenant_isolation ON {schema}.{table}; ALTER TABLE {schema}.{table} DISABLE ROW LEVEL SECURITY;");
        }

        migrationBuilder.Sql($"""
            ALTER DEFAULT PRIVILEGES IN SCHEMA {schema} REVOKE SELECT, INSERT, UPDATE, DELETE ON TABLES FROM {AppRole};
            REVOKE ALL ON ALL TABLES IN SCHEMA {schema} FROM {AppRole};
            REVOKE USAGE ON SCHEMA {schema} FROM {AppRole};
            """);
    }
}

/// <summary>
/// Sets app.tenant_id transaction-locally (set_config(..., true)), so the value never outlives the transaction on a
/// pooled connection.
/// <list type="bullet">
/// <item>Inside a transaction (every SaveChanges, explicit transactions): once, right after BEGIN.</item>
/// <item>A command with no transaction (most reads, ExecuteUpdate): in the same batch, which PostgreSQL runs as one
/// implicit transaction. A DO block returns no result set, so the reader still sees the command's rows first.</item>
/// </list>
/// Prefixing write batches instead breaks EF's per-statement row counts (DbUpdateConcurrencyException).
/// </summary>
internal sealed class TenantCommandInterceptor(ITenantContext tenant)
    : Microsoft.EntityFrameworkCore.Diagnostics.DbCommandInterceptor, Microsoft.EntityFrameworkCore.Diagnostics.IDbTransactionInterceptor
{
    public override System.Data.Common.DbCommand CommandInitialized(
        Microsoft.EntityFrameworkCore.Diagnostics.CommandEndEventData eventData, System.Data.Common.DbCommand result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (tenant.TenantId is { } id && result.Transaction is null)
        {
            // The Guid is formatted by .NET, so it cannot carry SQL.
            result.CommandText = $"DO $tenant$ BEGIN PERFORM set_config('{RowLevelSecurity.Setting}', '{id:D}', true); END $tenant$;\n" + result.CommandText;
        }

        return result;
    }

    public System.Data.Common.DbTransaction TransactionStarted(
        System.Data.Common.DbConnection connection, Microsoft.EntityFrameworkCore.Diagnostics.TransactionEndEventData eventData, System.Data.Common.DbTransaction result)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (tenant.TenantId is { } id)
        {
            using var command = SetTenant(connection, result, id);
            command.ExecuteNonQuery();
        }

        return result;
    }

    public async ValueTask<System.Data.Common.DbTransaction> TransactionStartedAsync(
        System.Data.Common.DbConnection connection, Microsoft.EntityFrameworkCore.Diagnostics.TransactionEndEventData eventData,
        System.Data.Common.DbTransaction result, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (tenant.TenantId is { } id)
        {
            await using var command = SetTenant(connection, result, id);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        return result;
    }

    private static System.Data.Common.DbCommand SetTenant(System.Data.Common.DbConnection connection, System.Data.Common.DbTransaction transaction, Guid id)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
#pragma warning disable CA2100 // the only value is a formatted Guid
        command.CommandText = $"SELECT set_config('{RowLevelSecurity.Setting}', '{id:D}', true)";
#pragma warning restore CA2100
        return command;
    }
}
