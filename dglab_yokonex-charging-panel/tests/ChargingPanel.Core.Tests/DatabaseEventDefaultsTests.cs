using ChargingPanel.Core.Data;
using Microsoft.Data.Sqlite;
using Xunit;

namespace ChargingPanel.Core.Tests;

public sealed class DatabaseEventDefaultsTests : IDisposable
{
    private readonly string _databasePath;

    public DatabaseEventDefaultsTests()
    {
        _databasePath = Path.Combine(
            Path.GetTempPath(),
            $"charging_panel_event_defaults_{Guid.NewGuid():N}.db");
        Database.Initialize(_databasePath);
    }

    [Theory]
    [InlineData("lost-ahp", "armor-decrease", 1, 100)]
    [InlineData("lost-hp", "hp-decrease", 1, 100)]
    [InlineData("add-ahp", "armor-increase", 1, 100)]
    [InlineData("add-hp", "hp-increase", 1, 100)]
    [InlineData("dead", "death", 0, 0)]
    [InlineData("respawn", "revive", 0, 0)]
    [InlineData("new-round", "new-round", 0, 0)]
    [InlineData("game-over", "game-over", 0, 0)]
    public void NewDatabase_DefaultSystemEvent_PersistsCanonicalTriggerProfile(
        string eventId,
        string expectedTriggerType,
        int expectedMinChange,
        int expectedMaxChange)
    {
        var record = Database.Instance.GetEventByEventId(eventId);

        Assert.NotNull(record);
        Assert.Equal("system", record.Category);
        Assert.Equal(expectedTriggerType, record.TriggerType);
        Assert.Equal(expectedMinChange, record.MinChange);
        Assert.Equal(expectedMaxChange, record.MaxChange);
    }

    [Fact]
    public void Initialize_LegacyUntouchedSystemDefaults_RepairsCanonicalProfilesIdempotently()
    {
        ExecuteSql("""
            UPDATE events
            SET triggerType = 'hp-decrease',
                minChange = 10,
                maxChange = 10,
                updatedAt = createdAt
            WHERE category = 'system';
            """);

        Database.Initialize(_databasePath);

        AssertCanonicalProfiles();
        var timestampsAfterRepair = Database.Instance.GetEventsByCategory("system")
            .ToDictionary(record => record.EventId, record => record.UpdatedAt, StringComparer.Ordinal);

        Database.Initialize(_databasePath);

        AssertCanonicalProfiles();
        var timestampsAfterSecondInitialize = Database.Instance.GetEventsByCategory("system")
            .ToDictionary(record => record.EventId, record => record.UpdatedAt, StringComparer.Ordinal);
        Assert.Equal(timestampsAfterRepair, timestampsAfterSecondInitialize);
    }

    [Fact]
    public void Initialize_UserCustomizedSystemDefault_DoesNotOverwriteTriggerProfile()
    {
        ExecuteSql("""
            UPDATE events
            SET triggerType = 'hp-increase',
                minChange = 7,
                maxChange = 77,
                updatedAt = '2099-01-01T00:00:00.0000000Z'
            WHERE eventId = 'lost-hp';
            """);

        Database.Initialize(_databasePath);

        var record = Database.Instance.GetEventByEventId("lost-hp");
        Assert.NotNull(record);
        Assert.Equal("hp-increase", record.TriggerType);
        Assert.Equal(7, record.MinChange);
        Assert.Equal(77, record.MaxChange);
        Assert.Equal("2099-01-01T00:00:00.0000000Z", record.UpdatedAt);
    }

    [Fact]
    public void Initialize_CustomCategoryOverride_DoesNotOverwriteTriggerProfile()
    {
        ExecuteSql("""
            UPDATE events
            SET category = 'custom',
                triggerType = 'hp-decrease',
                minChange = 10,
                maxChange = 10,
                updatedAt = createdAt
            WHERE eventId = 'lost-hp';
            """);

        Database.Initialize(_databasePath);

        var record = Database.Instance.GetEventByEventId("lost-hp");
        Assert.NotNull(record);
        Assert.Equal("custom", record.Category);
        Assert.Equal("hp-decrease", record.TriggerType);
        Assert.Equal(10, record.MinChange);
        Assert.Equal(10, record.MaxChange);
    }

    private void AssertCanonicalProfiles()
    {
        var expectedProfiles = new Dictionary<string, (string TriggerType, int MinChange, int MaxChange)>(StringComparer.Ordinal)
        {
            ["lost-ahp"] = ("armor-decrease", 1, 100),
            ["lost-hp"] = ("hp-decrease", 1, 100),
            ["add-ahp"] = ("armor-increase", 1, 100),
            ["add-hp"] = ("hp-increase", 1, 100),
            ["dead"] = ("death", 0, 0),
            ["respawn"] = ("revive", 0, 0),
            ["new-round"] = ("new-round", 0, 0),
            ["game-over"] = ("game-over", 0, 0)
        };

        foreach (var (eventId, expected) in expectedProfiles)
        {
            var record = Database.Instance.GetEventByEventId(eventId);
            Assert.NotNull(record);
            Assert.Equal(expected.TriggerType, record.TriggerType);
            Assert.Equal(expected.MinChange, record.MinChange);
            Assert.Equal(expected.MaxChange, record.MaxChange);
        }
    }

    private void ExecuteSql(string sql)
    {
        using var connection = new SqliteConnection($"Data Source={_databasePath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    public void Dispose()
    {
        try
        {
            Database.Instance.Dispose();
        }
        catch
        {
            // 测试清理由最佳努力完成。
        }

        SqliteConnection.ClearAllPools();
        DeleteIfExists(_databasePath);
        DeleteIfExists(_databasePath + "-shm");
        DeleteIfExists(_databasePath + "-wal");
    }

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}
