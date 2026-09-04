using System.Text.Json;
using System.Text.Json.Serialization;

namespace RootedAndroidGameVM.Core.Storage;

[JsonConverter(typeof(JsonStringEnumConverter<MigrationStage>))]
public enum MigrationStage { Copying, Prepared, Verified, Cleaning }

public sealed record MigrationJournal(Guid Id, string SourceRoot, string TargetRoot, string StagingRoot,
    MigrationStage Stage, ResourceInventory Inventory)
{
    public const string FileName = "storage-migration.json";

    public static MigrationJournal? Read(ProductStorageLocation location)
    {
        var path = Path.Combine(location.ControlRoot, FileName);
        if (!File.Exists(path)) return null;
        StoragePathPolicy.RejectReparsePoints(path);
        try
        {
            return JsonSerializer.Deserialize<MigrationJournal>(File.ReadAllText(path), AtomicJsonFile.Options)
                ?? throw new InvalidDataException("迁移记录为空。");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("迁移记录损坏，已停止操作以保留两处数据。", exception);
        }
    }
}
