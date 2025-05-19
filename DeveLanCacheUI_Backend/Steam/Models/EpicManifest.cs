namespace DeveLanCacheUI_Backend.Steam.Models
{
    /// <summary>
    /// Represents an Epic Games manifest structure
    /// </summary>
    public class EpicManifest
    {
        public string? GameName { get; set; }
        public string? AppName { get; set; }
        public string? VersionString { get; set; }
        public DateTime CreationTime { get; set; }

        /// <summary>
        /// Deserializes Epic manifest binary data
        /// </summary>
        /// <param name="data">The raw manifest data</param>
        /// <returns>Deserialized EpicManifest object</returns>
        public static EpicManifest? Deserialize(byte[] data)
        {
            try
            {
                using var ms = new MemoryStream(data);
                using var reader = new BinaryReader(ms);
                
                var manifest = new EpicManifest
                {
                    CreationTime = DateTime.UtcNow // Default to current time
                };

                // Try to extract the game name and other metadata
                // Epic manifests are typically JSON data that may be compressed
                // This is a simple implementation that tries to find readable strings
                
                try
                {
                    // Convert to string to search for JSON content
                    string contentAsString = Encoding.UTF8.GetString(data);
                    
                    // Look for common game metadata in the manifest
                    manifest.GameName = ExtractValue(contentAsString, "\"DisplayName\":");
                    manifest.AppName = ExtractValue(contentAsString, "\"AppName\":");
                    manifest.VersionString = ExtractValue(contentAsString, "\"VersionString\":");
                    
                    // If we found at least a game name, return the manifest
                    if (!string.IsNullOrWhiteSpace(manifest.GameName))
                    {
                        return manifest;
                    }
                }
                catch
                {
                    // Ignore parsing errors and try alternate methods
                }
                
                return manifest;
            }
            catch
            {
                return null;
            }
        }
        
        private static string? ExtractValue(string content, string key)
        {
            int startIndex = content.IndexOf(key);
            if (startIndex >= 0)
            {
                startIndex = content.IndexOf("\"", startIndex + key.Length);
                if (startIndex >= 0)
                {
                    startIndex++; // Skip the opening quote
                    int endIndex = content.IndexOf("\"", startIndex);
                    if (endIndex >= 0)
                    {
                        return content.Substring(startIndex, endIndex - startIndex);
                    }
                }
            }
            return null;
        }
    }
}