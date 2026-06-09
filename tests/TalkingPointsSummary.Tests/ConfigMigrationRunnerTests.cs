using FluentAssertions;
using Microsoft.Extensions.Configuration;
using TalkingPointsSummary.Configuration;

namespace TalkingPointsSummary.Tests;

public class ConfigMigrationRunnerTests
{
    // ── Helper ───────────────────────────────────────────────────────────────

    private static IConfiguration Config(Dictionary<string, string?> values)
        => new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    private static ConfigKeyMigration SimpleMigration(
        string oldKey = "Old:Key",
        string newKey = "New:Key",
        string message = "Test migration",
        IReadOnlyList<ConfigKeyCompanion>? companions = null)
        => new(oldKey, newKey, message, companions ?? []);

    // ── Core runner behavior ─────────────────────────────────────────────────

    [Fact]
    public void Run_OldKeySetNewKeyAbsent_PromotesValueAndEmitsWarning()
    {
        var migration = SimpleMigration();
        var config = Config(new() { ["Old:Key"] = "the-value" });

        var (promoted, warnings) = ConfigMigrationRunner.Run(config, [migration]);

        promoted.Should().ContainKey("New:Key").WhoseValue.Should().Be("the-value");
        warnings.Should().HaveCount(1).And.Contain("Test migration");
    }

    [Fact]
    public void Run_NewKeyAlreadySet_DoesNotOverwrite()
    {
        var migration = SimpleMigration();
        var config = Config(new() { ["Old:Key"] = "old", ["New:Key"] = "new" });

        var (promoted, warnings) = ConfigMigrationRunner.Run(config, [migration]);

        promoted.Should().BeEmpty();
        warnings.Should().BeEmpty();
    }

    [Fact]
    public void Run_OldKeyAbsent_NoMigration()
    {
        var migration = SimpleMigration();
        var config = Config(new());

        var (promoted, warnings) = ConfigMigrationRunner.Run(config, [migration]);

        promoted.Should().BeEmpty();
        warnings.Should().BeEmpty();
    }

    [Fact]
    public void Run_CompanionAbsent_SetsCompanion()
    {
        var migration = SimpleMigration(companions: [new ConfigKeyCompanion("Extra:Key", "extra-value")]);
        var config = Config(new() { ["Old:Key"] = "the-value" });

        var (promoted, warnings) = ConfigMigrationRunner.Run(config, [migration]);

        promoted.Should().ContainKey("Extra:Key").WhoseValue.Should().Be("extra-value");
    }

    [Fact]
    public void Run_CompanionAlreadySet_DoesNotOverwriteCompanion()
    {
        var migration = SimpleMigration(companions: [new ConfigKeyCompanion("Extra:Key", "default")]);
        var config = Config(new() { ["Old:Key"] = "v", ["Extra:Key"] = "already-set" });

        var (promoted, _) = ConfigMigrationRunner.Run(config, [migration]);

        promoted.Should().NotContainKey("Extra:Key");
    }

    [Fact]
    public void Run_EmptyMigrationList_ReturnsEmpty()
    {
        var config = Config(new() { ["Old:Key"] = "v" });

        var (promoted, warnings) = ConfigMigrationRunner.Run(config, []);

        promoted.Should().BeEmpty();
        warnings.Should().BeEmpty();
    }

    [Fact]
    public void Run_MultipleMigrations_AppliesAllThatFire()
    {
        var migrations = new[]
        {
            SimpleMigration("A:Old", "A:New", "Migrated A"),
            SimpleMigration("B:Old", "B:New", "Migrated B"),
            SimpleMigration("C:Old", "C:New", "Migrated C"),  // C:Old absent — should not fire
        };
        var config = Config(new() { ["A:Old"] = "a-val", ["B:Old"] = "b-val" });

        var (promoted, warnings) = ConfigMigrationRunner.Run(config, migrations);

        promoted.Should().ContainKey("A:New").WhoseValue.Should().Be("a-val");
        promoted.Should().ContainKey("B:New").WhoseValue.Should().Be("b-val");
        promoted.Should().NotContainKey("C:New");
        warnings.Should().HaveCount(2);
    }

    [Fact]
    public void Run_TwoMigrationsShareCompanion_FirstWins()
    {
        // Both migrations want to set the same companion; only the first to fire should win.
        var migrations = new[]
        {
            SimpleMigration("A:Old", "A:New", "A", [new ConfigKeyCompanion("Shared", "from-A")]),
            SimpleMigration("B:Old", "B:New", "B", [new ConfigKeyCompanion("Shared", "from-B")]),
        };
        var config = Config(new() { ["A:Old"] = "a", ["B:Old"] = "b" });

        var (promoted, _) = ConfigMigrationRunner.Run(config, migrations);

        promoted["Shared"].Should().Be("from-A");
    }

    // ── Registered migrations sanity checks ─────────────────────────────────

    [Fact]
    public void ConfigKeyMigrations_AnthropicApiKey_OldKeySetNewKeyAbsent_PromotesAndSetsProvider()
    {
        var config = Config(new() { ["Anthropic:ApiKey"] = "sk-legacy" });

        var (promoted, warnings) = ConfigMigrationRunner.Run(config, ConfigKeyMigrations.All);

        promoted.Should().ContainKey("Ai:Anthropic:ApiKey").WhoseValue.Should().Be("sk-legacy");
        promoted.Should().ContainKey("Ai:Provider").WhoseValue.Should().Be("Anthropic");
        warnings.Should().HaveCount(1);
        warnings[0].Should().Contain("Anthropic:ApiKey").And.Contain("Ai:Anthropic:ApiKey");
    }

    [Fact]
    public void ConfigKeyMigrations_AnthropicApiKey_NewKeyAlreadySet_NoPromotion()
    {
        var config = Config(new() { ["Anthropic:ApiKey"] = "old", ["Ai:Anthropic:ApiKey"] = "new", ["Ai:Provider"] = "Anthropic" });

        var (promoted, warnings) = ConfigMigrationRunner.Run(config, ConfigKeyMigrations.All);

        promoted.Should().BeEmpty();
        warnings.Should().BeEmpty();
    }

    [Fact]
    public void ConfigKeyMigrations_AnthropicApiKey_AiProviderAlreadySet_DoesNotOverwriteProvider()
    {
        var config = Config(new() { ["Anthropic:ApiKey"] = "sk-legacy", ["Ai:Provider"] = "CustomProvider" });

        var (promoted, _) = ConfigMigrationRunner.Run(config, ConfigKeyMigrations.All);

        promoted.Should().ContainKey("Ai:Anthropic:ApiKey");
        promoted.Should().NotContainKey("Ai:Provider");
    }
}
