namespace DeveLanCacheUI_Backend.Services
{
    public class EpicManifestService
    {
        private readonly IServiceProvider _services;
        private readonly IHttpClientFactory _httpClientFactoryForManifestDownloads;
        private readonly ILogger<EpicManifestService> _logger;
        private readonly string _manifestDirectory;

        private static SemaphoreSlim _semaphoreSlim = new SemaphoreSlim(1, 1);

        public EpicManifestService(DeveLanCacheConfiguration deveLanCacheConfiguration, IServiceProvider services, IHttpClientFactory httpClientFactory, ILogger<EpicManifestService> logger)
        {
            _services = services;
            _httpClientFactoryForManifestDownloads = httpClientFactory;
            _logger = logger;

            var deveLanCacheUIDataDirectory = deveLanCacheConfiguration.DeveLanCacheUIDataDirectory ?? string.Empty;
            _manifestDirectory = Path.Combine(deveLanCacheUIDataDirectory, "epicmanifests");
        }

        public void TryToDownloadManifest(LanCacheLogEntryRaw lanCacheLogEntryRaw)
        {
            if (!lanCacheLogEntryRaw.Request.Contains("/manifest/") || lanCacheLogEntryRaw.DownloadIdentifier == null)
            {
                _logger.LogError("Code bug: Trying to download Epic manifest that isn't actually a manifest: {OriginalLogLine}", lanCacheLogEntryRaw.OriginalLogLine);
                return;
            }

            _ = Task.Run(async () =>
            {
                var fallbackPolicy = Policy
                    .Handle<Exception>()
                    .FallbackAsync(async (ct) =>
                    {
                        await Task.CompletedTask;
                        _logger.LogInformation("Epic manifest saving: All retries failed, skipping...");
                    });

                var retryPolicy = Policy
                   .Handle<Exception>()
                   .WaitAndRetryAsync(3, retryAttempt => TimeSpan.FromSeconds(Math.Pow(2, retryAttempt)),
                   (exception, timeSpan, context) =>
                   {
                       _logger.LogInformation("Epic manifest saving: An error occurred while trying to save changes: {Message}", exception.Message);
                   });

                await fallbackPolicy.WrapAsync(retryPolicy).ExecuteAsync(async () =>
                {
                    try
                    {
                        _semaphoreSlim.Wait();
                        await using (var scope = _services.CreateAsyncScope())
                        {
                            var theManifestUrlPart = lanCacheLogEntryRaw.Request.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1];

                            var everythingAfterManifest = theManifestUrlPart.Split("/manifest/", StringSplitOptions.RemoveEmptyEntries).Last();
                            var manifestId = everythingAfterManifest.Split("/", StringSplitOptions.RemoveEmptyEntries).First();

                            // Create a safe filename for the manifest
                            var manifestIdFileName = Path.GetInvalidFileNameChars()
                                .Aggregate(manifestId, (current, c) => current.Replace(c.ToString(), "_")) + ".bin";
                            
                            var uniqueManifestIdentifier = manifestIdFileName;

                            using var dbContext = scope.ServiceProvider.GetRequiredService<DeveLanCacheUIDbContext>();
                            var dbManifestFound = await dbContext.EpicManifests.FirstOrDefaultAsync(t => t.UniqueManifestIdentifier == uniqueManifestIdentifier);

                            if (dbManifestFound != null)
                            {
                                return;
                            }

                            var fullPath = Path.Combine(_manifestDirectory, uniqueManifestIdentifier);
                            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);

                            var cachedUrl = $"http://lancache.epicgames.com{theManifestUrlPart}";
                            using var httpClient = _httpClientFactoryForManifestDownloads.CreateClient();
                            httpClient.DefaultRequestHeaders.Add("Host", lanCacheLogEntryRaw.Host);
                            httpClient.DefaultRequestHeaders.Add("User-Agent", "EpicGamesLauncher/13.0.0");
                            httpClient.DefaultRequestHeaders.Referrer = LanCacheLogReaderHostedService.SkipLogLineReferrer; //Add this to ensure we don't process this line again
                            var manifestResponse = await httpClient.GetAsync(cachedUrl);

                            if (!manifestResponse.IsSuccessStatusCode)
                            {
                                _logger.LogWarning("Warning: Tried to obtain Epic manifest but status code was: {StatusCode}", manifestResponse.StatusCode);
                                return;
                            }
                            var manifestBytes = await manifestResponse.Content.ReadAsByteArrayAsync();

                            var dbManifest = ManifestBytesToDbEpicManifest(manifestBytes, uniqueManifestIdentifier);

                            if (dbManifest == null)
                            {
                                _logger.LogWarning("Could not parse Epic manifest: {ManifestId}", manifestId);
                                return;
                            }

                            var dbValue = dbContext.EpicManifests.FirstOrDefault(t => t.UniqueManifestIdentifier == dbManifest.UniqueManifestIdentifier);
                            if (dbValue != null)
                            {
                                dbContext.Entry(dbValue).CurrentValues.SetValues(dbManifest);
                                _logger.LogInformation("Updated Epic manifest {ManifestId}", manifestId);
                            }
                            else
                            {
                                await dbContext.EpicManifests.AddAsync(dbManifest);
                                _logger.LogInformation("Added Epic manifest {ManifestId}", manifestId);
                            }

                            await File.WriteAllBytesAsync(fullPath, manifestBytes);
                            await dbContext.SaveChangesAsync();
                        }
                    }
                    finally
                    {
                        _semaphoreSlim.Release();
                    }
                });
            });
        }

        private DbEpicManifest? ManifestBytesToDbEpicManifest(byte[] manifestBytes, string uniqueManifestIdentifier)
        {
            var epicManifest = EpicManifest.Deserialize(manifestBytes);

            if (epicManifest == null)
            {
                return null;
            }

            var dbEpicManifest = new DbEpicManifest()
            {
                GameName = epicManifest.GameName ?? "Unknown",
                AppName = epicManifest.AppName,
                VersionString = epicManifest.VersionString,
                CreationTime = epicManifest.CreationTime,
                ManifestBytesSize = (ulong)manifestBytes.LongLength,
                UniqueManifestIdentifier = uniqueManifestIdentifier
            };

            return dbEpicManifest;
        }

        public static string ManifestBytesToJson(byte[] manifestBytes, bool indented)
        {
            var epicManifest = EpicManifest.Deserialize(manifestBytes);
            var json = JsonSerializer.Serialize(epicManifest, new JsonSerializerOptions() { WriteIndented = indented });
            return json;
        }

        public static JsonDocument ManifestBytesToJsonValue(byte[] manifestBytes)
        {
            var json = ManifestBytesToJson(manifestBytes, false);
            var document = JsonDocument.Parse(json);
            return document;
        }

        public byte[]? GetBytesForUniqueManifestIdentifier(string uniqueManifestIdentifier)
        {
            var fullPath = Path.Combine(_manifestDirectory, uniqueManifestIdentifier);
            if (File.Exists(fullPath))
            {
                return File.ReadAllBytes(fullPath);
            }
            else
            {
                return null;
            }
        }
    }
}