namespace VintageStorySaveManager;

internal interface ISaveCodec
{
    SaveMetadata Read(ReadOnlySpan<byte> payload);
    byte[] SetWorldName(ReadOnlySpan<byte> payload, string worldName);
    RepairIdentity ReadRepairIdentity(ReadOnlySpan<byte> payload);
    IRepairValidator CreateRepairValidator(string gameVersion);
}

internal interface IRepairValidator : IDisposable
{
    // Return a reason only for invalid data. Runtime/dependency failures must throw.
    string? Validate(string table, byte[]? payload);
}

internal sealed record RepairIdentity(
    string WorldId, int Seed, int MapSizeX, int MapSizeY, int MapSizeZ,
    string CreatedGameVersion, string LastSavedGameVersion, int CreatedWorldGenVersion);

internal sealed record RepairIssue(string Table, long Position, string Reason, bool ReplacementAvailable, string? BackupError);

internal sealed record RepairTableSummary(string Table, int Total, int Damaged, int Repairable);

internal sealed record RepairReport(
    string SourceId, string? BackupId, int SourcePlayedSeconds, int? BackupPlayedSeconds,
    IReadOnlyList<RepairTableSummary> Tables, IReadOnlyList<RepairIssue> Issues)
{
    public int DamagedRecords => Issues.Count;
    public int UnresolvedRecords => Issues.Count(issue => !issue.ReplacementAvailable);
    public bool CanRepair => DamagedRecords > 0 && UnresolvedRecords == 0;

    public string Summary => string.Join(Environment.NewLine,
        Tables.Select(table => $"{table.Table}: {table.Damaged} damaged / {table.Total}; {table.Repairable} replaceable")) +
        $"\nUnresolved records: {UnresolvedRecords}.";

    public const string Limitations = "Restored records revert to the backup's state; builds, containers, and entities in those chunks may roll back. " +
        "Player data and world metadata are preserved. Missing rows and damage inside otherwise decodable mod data are not detected. Test the new save in the game.";
}
