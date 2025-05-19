namespace DeveLanCacheUI_Backend.Controllers.Models
{
    public class EpicManifest
    {
        public required string GameName { get; set; }
        public string? AppName { get; set; }
        public string? VersionString { get; set; }
        public required DateTime CreationTime { get; set; }
        public required JsonDocument? ProtobufDataAsJson { get; set; }
        public required string UniqueManifestIdentifier { get; set; }
        public ulong ManifestBytesSize { get; set; }
    }
}