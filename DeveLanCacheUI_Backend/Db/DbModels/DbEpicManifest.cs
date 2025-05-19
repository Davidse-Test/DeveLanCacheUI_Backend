namespace DeveLanCacheUI_Backend.Db.DbModels
{
    [Index(nameof(UniqueManifestIdentifier), IsUnique = true)]
    public class DbEpicManifest
    {
        public required string GameName { get; set; }
        public string? AppName { get; set; }
        public string? VersionString { get; set; }
        public required DateTime CreationTime { get; set; }
        public required string UniqueManifestIdentifier { get; set; }
        public required ulong ManifestBytesSize { get; set; }
    }
}