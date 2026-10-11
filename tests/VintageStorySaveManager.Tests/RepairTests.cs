using System.Text.Json;
using Microsoft.Data.Sqlite;
using VintageStorySaveManager;

namespace VintageStorySaveManager.Tests;

public sealed class RepairTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"vsm-repair-tests-{Guid.NewGuid():N}");
    private readonly SaveLocation _location;
    private readonly SaveRepository _repository;
    private readonly FakeCodec _codec = new();
    private readonly string _source;
    private readonly string _backup;
    private static readonly RepairIdentity Identity = new("world-a", 123, 1024, 256, 1024, "1.22.7", "1.22.7", 1);

    static RepairTests() => SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_sqlite3());

    public RepairTests()
    {
        var saves = Path.Combine(_root, "Saves");
        var backups = Path.Combine(saves, "backup");
        Directory.CreateDirectory(backups);
        _location = new SaveLocation("test", _root, saves, backups);
        _repository = new SaveRepository([_location], _codec);
        _source = Path.Combine(saves, "main.vcdbs");
        _backup = Path.Combine(backups, "backup.vcdbs");
        Create(_source, new FixtureMetadata(Identity, 200));
        Create(_backup, new FixtureMetadata(Identity, 100));
    }

    [Fact]
    public void ScanFindsEmptyMalformedAndNullPayloadsWithoutChangingSources()
    {
        Set(_source, "mapregion", 1, []);
        Set(_source, "mapchunk", 2, [0]);
        Set(_source, "chunk", 3, null);
        var sourceBytes = File.ReadAllBytes(_source);
        var backupBytes = File.ReadAllBytes(_backup);

        using var session = Prepare();

        Assert.True(session.Report.CanRepair);
        Assert.Equal(3, session.Report.DamagedRecords);
        Assert.All(session.Report.Tables, table => Assert.Equal(3, table.Total));
        Assert.Equal(sourceBytes, File.ReadAllBytes(_source));
        Assert.Equal(backupBytes, File.ReadAllBytes(_backup));
        Assert.Single(Directory.GetFiles(_location.SavesDirectory, "*.vcdbs"));
    }

    [Fact]
    public void RepairCopiesOnlyDamagedRecordsAndPreservesMetadataAndPlayerData()
    {
        Set(_source, "mapregion", 1, []);
        Set(_source, "mapchunk", 2, [0]);
        Set(_source, "chunk", 3, null);
        Set(_source, "chunk", 1, [1, 77]); // Newer, valid content must survive.
        var sourceBytes = File.ReadAllBytes(_source);
        using var session = Prepare();

        var result = _repository.CompleteRepair(session, "repaired copy");

        Assert.Equal("World", result.WorldName);
        Assert.Equal(200, result.Metadata!.TotalSecondsPlayed);
        Assert.Equal(sourceBytes, File.ReadAllBytes(_source));
        foreach (var table in new[] { "mapregion", "mapchunk", "chunk" })
        {
            for (var position = 1; position <= 3; position++)
            {
                var replacement = session.Report.Issues.Any(issue => issue.Table == table && issue.Position == position);
                Assert.Equal(Read(replacement ? _backup : _source, table, position), Read(result.Path, table, position));
            }
        }

        Assert.Equal(Read(_source, "gamedata", 1, "savegameid"), Read(result.Path, "gamedata", 1, "savegameid"));
        Assert.Equal(Read(_source, "playerdata", 1, "playerid"), Read(result.Path, "playerdata", 1, "playerid"));
        AssertNoStagingFiles();
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("invalid")]
    [InlineData("empty")]
    [InlineData("null")]
    public void UnusableReplacementIsReportedAndPreventsAnyOutput(string kind)
    {
        Set(_source, "chunk", 1, [0]);
        if (kind == "missing")
        {
            Execute(_backup, "DELETE FROM chunk WHERE position = 1");
        }
        else
        {
            Set(_backup, "chunk", 1, kind switch { "invalid" => [0], "empty" => [], _ => null });
        }

        using var session = Prepare();

        Assert.False(session.Report.CanRepair);
        Assert.Equal(1, session.Report.UnresolvedRecords);
        Assert.NotNull(Assert.Single(session.Report.Issues).BackupError);
        Assert.Throws<UserFacingException>(() => _repository.CompleteRepair(session, "repaired"));
        Assert.False(File.Exists(Path.Combine(_location.SavesDirectory, "repaired.vcdbs")));
        AssertNoStagingFiles();
    }

    [Fact]
    public void CanScanWithoutBackup()
    {
        Set(_source, "chunk", 1, [0]);
        using var session = _repository.PrepareRepair(_repository.ResolveSave("main.vcdbs"));
        Assert.Null(session.Report.BackupId);
        Assert.False(session.Report.CanRepair);
        Assert.Equal(1, session.Report.DamagedRecords);
    }

    [Theory]
    [InlineData("id")]
    [InlineData("seed")]
    [InlineData("size")]
    [InlineData("version")]
    [InlineData("generation")]
    public void RejectsUnrelatedOrIncompatibleBackup(string field)
    {
        var identity = field switch
        {
            "id" => Identity with { WorldId = "other" },
            "seed" => Identity with { Seed = 999 },
            "size" => Identity with { MapSizeY = 512 },
            "version" => Identity with { LastSavedGameVersion = "1.21.0" },
            _ => Identity with { CreatedWorldGenVersion = 2 }
        };
        Set(_backup, "gamedata", 1, JsonSerializer.SerializeToUtf8Bytes(new FixtureMetadata(identity, 100)), "savegameid");
        Assert.Contains("different world", Assert.Throws<UserFacingException>(() => Prepare()).Message);
    }

    [Fact]
    public void RejectsBackupFromFuturePlaytime()
    {
        Set(_backup, "gamedata", 1, JsonSerializer.SerializeToUtf8Bytes(new FixtureMetadata(Identity, 201)), "savegameid");
        Assert.Contains("ahead", Assert.Throws<UserFacingException>(() => Prepare()).Message);
    }

    [Fact]
    public void RefusesExistingOrEscapingDestination()
    {
        Set(_source, "chunk", 1, [0]);
        var original = File.ReadAllBytes(_source);
        using var session = Prepare();
        Assert.Throws<UserFacingException>(() => _repository.CompleteRepair(session, "main"));
        Assert.Throws<UserFacingException>(() => _repository.CompleteRepair(session, "../escape"));
        Assert.Equal(original, File.ReadAllBytes(_source));
        AssertNoStagingFiles();
    }

    [Fact]
    public void PreparedRepairUsesSnapshotsEvenIfLiveFilesChange()
    {
        Set(_source, "chunk", 1, [0]);
        var expected = Read(_backup, "chunk", 1);
        using var session = Prepare();
        Set(_source, "chunk", 2, [1, 77]);
        Set(_backup, "chunk", 1, [0]);
        var result = _repository.CompleteRepair(session, "repaired");
        Assert.Equal(expected, Read(result.Path, "chunk", 1));
        Assert.Equal(new byte[] { 1, 2 }, Read(result.Path, "chunk", 2));
        Assert.Equal(new byte[] { 1, 77 }, Read(_source, "chunk", 2));
    }

    [Fact]
    public void NoDamageDoesNotCreateACopy()
    {
        using var session = Prepare();
        Assert.Empty(session.Report.Issues);
        Assert.Throws<UserFacingException>(() => _repository.CompleteRepair(session, "unneeded"));
        AssertNoStagingFiles();
    }

    [Fact]
    public void CancellationDoesNotPublishACopy()
    {
        Set(_source, "chunk", 1, [0]);
        using var session = Prepare();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => _repository.CompleteRepair(session, "cancelled", cancellation.Token));
        Assert.False(File.Exists(Path.Combine(_location.SavesDirectory, "cancelled.vcdbs")));
        AssertNoStagingFiles();
    }

    [Fact]
    public void FailedFinalValidationCleansUpOutputAndKeepsSource()
    {
        Set(_source, "chunk", 1, [0]);
        var original = File.ReadAllBytes(_source);
        using var session = Prepare();
        _codec.FailValidation = true;
        Assert.Throws<UserFacingException>(() => _repository.CompleteRepair(session, "failed"));
        Assert.False(File.Exists(Path.Combine(_location.SavesDirectory, "failed.vcdbs")));
        Assert.Equal(original, File.ReadAllBytes(_source));
        AssertNoStagingFiles();
    }

    [Fact]
    public void DecoderFailureAbortsInsteadOfCallingRecordsCorrupt()
    {
        _codec.FailValidation = true;
        Assert.Contains("decoder", Assert.Throws<UserFacingException>(() => Prepare()).Message);
    }

    [Fact]
    public void RejectsUnexpectedSchemaAndTriggers()
    {
        Execute(_source, "CREATE TRIGGER alter_metadata AFTER UPDATE ON chunk BEGIN DELETE FROM gamedata; END");
        Assert.Contains("triggers", Assert.Throws<UserFacingException>(() => Prepare()).Message);
        Execute(_source, "DROP TRIGGER alter_metadata; ALTER TABLE chunk ADD COLUMN surprise TEXT");
        Assert.Contains("schema", Assert.Throws<UserFacingException>(() => Prepare()).Message);
    }

    [Fact]
    public void RejectsDamagedSqliteWithoutChangingIt()
    {
        File.WriteAllBytes(_source, [1, 2, 3]);
        Assert.Throws<UserFacingException>(() => Prepare());
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(_source));
    }

    [Fact]
    public void QualifiedIdsAreRequiredForAmbiguousSaveNames()
    {
        var other = _location with { Name = "other", SavesDirectory = Path.Combine(_root, "Other") };
        Directory.CreateDirectory(other.SavesDirectory);
        Create(Path.Combine(other.SavesDirectory, "main.vcdbs"), new FixtureMetadata(Identity, 200));
        var repository = new SaveRepository([_location, other], _codec);
        Assert.Throws<UserFacingException>(() => repository.ResolveSave("main.vcdbs"));
        Assert.Equal(_source, repository.ResolveSave("test:main.vcdbs").Path);
    }

    [Fact]
    public void CliJsonScanIsOneMachineReadableDocumentAndCreatesNoSave()
    {
        Set(_source, "chunk", 1, [0]);
        var original = Console.Out;
        using var output = new StringWriter();
        try
        {
            Console.SetOut(output);
            Assert.Equal(0, new CommandLine(_repository).Run(["repair", "--save", "main.vcdbs", "--backup", "backup.vcdbs", "--json"]));
        }
        finally { Console.SetOut(original); }

        using var document = JsonDocument.Parse(output.ToString());
        Assert.Equal("scan", document.RootElement.GetProperty("Mode").GetString());
        Assert.Equal(1, document.RootElement.GetProperty("Report").GetProperty("DamagedRecords").GetInt32());
        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("Output").ValueKind);
        Assert.Single(Directory.GetFiles(_location.SavesDirectory, "*.vcdbs"));
    }

    [Theory]
    [InlineData("--destination", "main.vcdbs")]
    [InlineData("--new", "")]
    [InlineData("--json", "unexpected")]
    public void RepairCliRejectsUnsafeOrMistypedOptions(string option, string value)
    {
        Assert.Throws<UserFacingException>(() => new CommandLine(_repository).Run(["repair", "--save", "main.vcdbs", option, value]));
    }

    private RepairSession Prepare() => _repository.PrepareRepair(_repository.ResolveSave("main.vcdbs"), _repository.ResolveBackup("backup.vcdbs"));

    private void AssertNoStagingFiles() => Assert.Empty(Directory.GetFiles(_location.SavesDirectory, ".*"));

    private static void Create(string path, FixtureMetadata metadata)
    {
        Execute(path, "CREATE TABLE gamedata(savegameid INTEGER PRIMARY KEY, data BLOB); " +
            "CREATE TABLE playerdata(playerid INTEGER PRIMARY KEY, playeruid TEXT, data BLOB); " +
            "CREATE TABLE mapregion(position INTEGER PRIMARY KEY, data BLOB); " +
            "CREATE TABLE mapchunk(position INTEGER PRIMARY KEY, data BLOB); " +
            "CREATE TABLE chunk(position INTEGER PRIMARY KEY, data BLOB); " +
            "INSERT INTO gamedata VALUES(1, X'00'); INSERT INTO playerdata VALUES(1, 'player', X'017B');");
        Set(path, "gamedata", 1, JsonSerializer.SerializeToUtf8Bytes(metadata), "savegameid");
        foreach (var table in new[] { "mapregion", "mapchunk", "chunk" })
        {
            Execute(path, $"INSERT INTO {table} VALUES (1, X'0101'), (2, X'0102'), (3, X'0103')");
        }
    }

    private static void Execute(string path, string sql)
    {
        using var connection = SaveRepository.Open(path, SqliteOpenMode.ReadWriteCreate);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static void Set(string path, string table, long position, byte[]? data, string key = "position")
    {
        using var connection = SaveRepository.Open(path, SqliteOpenMode.ReadWrite);
        using var command = connection.CreateCommand();
        command.CommandText = $"UPDATE {table} SET data = $data WHERE {key} = $position";
        command.Parameters.AddWithValue("$data", (object?)data ?? DBNull.Value);
        command.Parameters.AddWithValue("$position", position);
        command.ExecuteNonQuery();
    }

    private static byte[] Read(string path, string table, long position, string key = "position")
    {
        using var connection = SaveRepository.Open(path, SqliteOpenMode.ReadOnly);
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT data FROM {table} WHERE {key} = $position";
        command.Parameters.AddWithValue("$position", position);
        return (byte[])command.ExecuteScalar()!;
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private sealed record FixtureMetadata(RepairIdentity Identity, int Played);

    private sealed class FakeCodec : ISaveCodec
    {
        public bool FailValidation { get; set; }
        private static FixtureMetadata Decode(ReadOnlySpan<byte> payload) => JsonSerializer.Deserialize<FixtureMetadata>(payload)!;
        public SaveMetadata Read(ReadOnlySpan<byte> payload) => new("World", Decode(payload).Played, "1.22.7", "1.22.7", null);
        public byte[] SetWorldName(ReadOnlySpan<byte> payload, string worldName) => throw new NotSupportedException();
        public RepairIdentity ReadRepairIdentity(ReadOnlySpan<byte> payload) => Decode(payload).Identity;
        public IRepairValidator CreateRepairValidator(string gameVersion) => new Validator(this);

        private sealed class Validator(FakeCodec codec) : IRepairValidator
        {
            public string? Validate(string table, byte[]? payload)
            {
                if (codec.FailValidation) throw new UserFacingException("The game decoder failed to initialize.");
                return payload is { Length: > 0 } && payload[0] == 1 ? null : "Invalid test payload.";
            }
            public void Dispose() { }
        }
    }
}
