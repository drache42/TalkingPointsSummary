using Microsoft.Extensions.Configuration;

namespace TalkingPointsSummary.Configuration;

/// <summary>
/// Applies a list of <see cref="ConfigKeyMigration"/> rules against a live
/// <see cref="IConfiguration"/> snapshot and returns the promoted key/value pairs
/// plus a deprecation warning for every migration that fired.
/// </summary>
internal static class ConfigMigrationRunner
{
    /// <summary>
    /// Runs all <paramref name="migrations"/> against <paramref name="config"/> and
    /// returns the values that should be injected under their new key names, along
    /// with a warning message for every migration that was applied.
    /// </summary>
    internal static (Dictionary<string, string?> Promoted, List<string> Warnings) Run(
        IConfiguration config,
        IReadOnlyList<ConfigKeyMigration> migrations)
    {
        var promoted = new Dictionary<string, string?>();
        var warnings = new List<string>();

        foreach (var migration in migrations)
        {
            var oldValue = config[migration.OldKey];

            // Skip when the old key is absent or the new key is already populated.
            if (string.IsNullOrEmpty(oldValue) || !string.IsNullOrEmpty(config[migration.NewKey]))
                continue;

            promoted[migration.NewKey] = oldValue;
            warnings.Add(migration.DeprecationMessage);

            // Apply companions only when they are absent in both the live config
            // and the values already being promoted by this run.
            foreach (var companion in migration.Companions)
            {
                if (string.IsNullOrEmpty(config[companion.Key]) && !promoted.ContainsKey(companion.Key))
                    promoted[companion.Key] = companion.Value;
            }
        }

        return (promoted, warnings);
    }
}
