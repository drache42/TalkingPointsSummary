namespace TalkingPointsSummary.Configuration;

/// <summary>
/// Registry of all configuration key migrations applied at startup.
/// To add a migration: append a new <see cref="ConfigKeyMigration"/> entry to <see cref="All"/>.
/// </summary>
internal static class ConfigKeyMigrations
{
    /// <summary>
    /// All registered migrations, applied in order by <see cref="ConfigMigrationRunner"/>.
    /// </summary>
    internal static readonly IReadOnlyList<ConfigKeyMigration> All =
    [
        // v1 → v2: flat Anthropic:ApiKey moved under the Ai: provider hierarchy.
        new ConfigKeyMigration(
            OldKey: "Anthropic:ApiKey",
            NewKey: "Ai:Anthropic:ApiKey",
            DeprecationMessage:
                "Deprecated config key 'Anthropic:ApiKey' was automatically migrated to " +
                "'Ai:Anthropic:ApiKey'. Update your environment variables or secrets to remove this warning.",
            Companions:
            [
                // Ensure Ai:Provider defaults to Anthropic when migrating from the old key.
                new ConfigKeyCompanion("Ai:Provider", "Anthropic")
            ]),
    ];
}
