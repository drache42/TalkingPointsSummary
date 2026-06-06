namespace TalkingPointsSummary.Configuration;

/// <summary>
/// A companion key/value pair that is written alongside a primary key migration,
/// but only when the companion key is not already populated in the live config.
/// </summary>
/// <param name="Key">Configuration key to set.</param>
/// <param name="Value">Value to assign when the key is absent.</param>
internal record ConfigKeyCompanion(string Key, string Value);

/// <summary>
/// Describes a single configuration key rename migration.
/// The migration fires when <see cref="OldKey"/> is populated and <see cref="NewKey"/> is absent.
/// </summary>
/// <param name="OldKey">Deprecated configuration key to read from.</param>
/// <param name="NewKey">Replacement configuration key to promote the value into.</param>
/// <param name="DeprecationMessage">Warning logged when this migration is applied.</param>
/// <param name="Companions">
/// Additional key/value pairs written when the migration fires and each companion key is absent.
/// </param>
internal record ConfigKeyMigration(
    string OldKey,
    string NewKey,
    string DeprecationMessage,
    IReadOnlyList<ConfigKeyCompanion> Companions);
