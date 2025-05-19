using DbContext = DeveLanCacheUI_Backend.Db.DeveLanCacheUIDbContext;
using System.IO.Compression;

namespace DeveLanCacheUI_Backend.LogReading
{
    public class LanCacheLogReaderHostedService : BackgroundService
    {
        public static Uri SkipLogLineReferrer = new Uri("http://develancacheui_skipthislogline");
        public static string SkipLogLineReferrerString = SkipLogLineReferrer.ToString();

        private readonly IServiceProvider _services;
        private readonly DeveLanCacheConfiguration _deveLanCacheConfiguration;
        private readonly SteamManifestService _steamManifestService;
        private readonly ILogger<LanCacheLogReaderHostedService> _logger;

        /// <summary>
        /// These app ids will be excluded from processing as they introduce a lot of noise in the logs.
        /// These are primarily the "Direct X Runtime" and ".NET Runtime" installers that are shared by nearly
        /// every single game on Steam.  Since they're so small and so frequent, excluding them should make the
        /// remaining logs far more usable.
        /// </summary>
        private readonly HashSet<string> ExcludedAppIds = new HashSet<string>()
        {
            //"229033",
            //"229000",
            //"229001",
            //"229002",
            //"229003",
            //"229004",
            //"229005",
            //"229006",
            //"229007",
            //"228981",
            //"228982",
            //"228983",
            //"228984",
            //"228985",
            //"228986",
            //"228987",
            //"228988",
            //"228989",
            //"228990"
        };

        public LanCacheLogReaderHostedService(IServiceProvider services,
            DeveLanCacheConfiguration deveLanCacheConfiguration,
            SteamManifestService steamManifestService,
            ILogger<LanCacheLogReaderHostedService> logger)
        {
            _services = services;
            _deveLanCacheConfiguration = deveLanCacheConfiguration;
            _steamManifestService = steamManifestService;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
            await GoRun(stoppingToken);
        }

        private async Task GoRun(CancellationToken stoppingToken)
        {
            var oldestLog = DateTime.MinValue;
            await using (var scope = _services.CreateAsyncScope())
            {
                using var dbContext = scope.ServiceProvider.GetRequiredService<DbContext>();
                var lastUpdatedItem = await dbContext.DownloadEvents.OrderByDescending(t => t.LastUpdatedAt).FirstOrDefaultAsync();
                if (lastUpdatedItem != null)
                {
                    oldestLog = lastUpdatedItem.LastUpdatedAt;
                }
                var totalByteReadSetting = await dbContext.Settings.FirstOrDefaultAsync(t => t.Key == DbSetting.SettingKey_TotalBytesRead);

                if (_deveLanCacheConfiguration.Feature_SkipLinesBasedOnBytesRead && long.TryParse(totalByteReadSetting?.Value, out var result))
                {
                    TotalBytesRead = result;
                }
            }

            var logFilePath = _deveLanCacheConfiguration.LanCacheLogsDirectory;
            if (logFilePath == null)
            {
                throw new NullReferenceException("LanCacheLogsDirectory == null, please ensure the LanCacheLogsDirectory ENVIRONMENT_VARIABLE is filled in");
            }

            var logFiles = GetLogFilesToProcess(logFilePath);
            
            foreach (var logFile in logFiles)
            {
                _logger.LogInformation("Processing log file: {LogFile}", logFile);
                
                // Reset total bytes read for each file except the main access.log
                if (Path.GetFileName(logFile) != "access.log")
                {
                    // For rotated or compressed logs, we want to read the whole file
                    TotalBytesRead = 0;
                }
                
                using (var stream = CreateStreamForLogFile(logFile))
                {
                    if (stream == null)
                    {
                        _logger.LogWarning("Could not create stream for log file: {LogFile}", logFile);
                        continue;
                    }

                    var allLogLines = TailFrom2(stream, stoppingToken);
                    var parsedLogLines = allLogLines.Select(t => t == null ? null : LanCacheLogLineParser.ParseLogEntry(t));
                    var batches = Batch2(parsedLogLines, 5000, oldestLog);

                    int totalLinesProcessed = 0;

                    foreach (var currentSet in batches)
                    {
                        _logger.LogInformation("Processing {Count} lines from {LogFile}... First DateTime: {FirstDate} (Total processed: {TotalLinesProcessed})",
                            currentSet.Count, Path.GetFileName(logFile), currentSet.FirstOrDefault()?.DateTime, totalLinesProcessed);
                        totalLinesProcessed += currentSet.Count;

                        await using (var scope = _services.CreateAsyncScope())
                        {
                            var retryPolicy = Policy
                                .Handle<DbUpdateException>()
                                .WaitAndRetryAsync(3, retryAttempt => TimeSpan.FromSeconds(Math.Pow(2, retryAttempt)),
                                (exception, timeSpan, context) =>
                                {
                                    _logger.LogError("An error occurred while trying to save changes: {Message}", exception.Message);
                                });

                            await retryPolicy.ExecuteAsync(async () =>
                            {
                                using var dbContext = scope.ServiceProvider.GetRequiredService<DbContext>();

                                //var filteredLogLines = currentSet.Where(t => t.CacheIdentifier == "steam");
                                IEnumerable<LanCacheLogEntryRaw> filteredLogLines = currentSet;
                                filteredLogLines = filteredLogLines.Where(t => t.CacheIdentifier != "127.0.0.1");
                                filteredLogLines = filteredLogLines.Where(t => t.Referer != SkipLogLineReferrerString);

                                Dictionary<string, DbDownloadEvent> steamAppDownloadEventsCache = new Dictionary<string, DbDownloadEvent>();

                                foreach (var lanCacheLogLine in filteredLogLines)
                                {
                                    if (lanCacheLogLine.CacheIdentifier == "steam" && ExcludedAppIds.Contains(lanCacheLogLine.DownloadIdentifier))
                                    {
                                        continue;
                                    }
                                    if (lanCacheLogLine.CacheIdentifier == "steam" && lanCacheLogLine.Request.Contains("/manifest/") && DateTime.Now < lanCacheLogLine.DateTime.AddDays(14))
                                    {
                                        _logger.LogInformation("Found manifest for Depot: {DownloadIdentifier}", lanCacheLogLine.DownloadIdentifier);
                                        var ttt = lanCacheLogLine;
                                        _steamManifestService.TryToDownloadManifest(ttt);
                                    }

                                    var cacheKey = $"{lanCacheLogLine.CacheIdentifier}_||_{lanCacheLogLine.DownloadIdentifier}_||_{lanCacheLogLine.RemoteAddress}";
                                    steamAppDownloadEventsCache.TryGetValue(cacheKey, out var cachedEvent);

                                    if (cachedEvent == null)
                                    {
                                        cachedEvent = await dbContext.DownloadEvents
                                           .FirstOrDefaultAsync(t =>
                                               t.CacheIdentifier == lanCacheLogLine.CacheIdentifier &&
                                               t.DownloadIdentifierString == lanCacheLogLine.DownloadIdentifier &&
                                               t.ClientIp == lanCacheLogLine.RemoteAddress &&
                                               t.LastUpdatedAt > lanCacheLogLine.DateTime.AddMinutes(-5)
                                               );
                                        if (cachedEvent != null)
                                        {
                                            steamAppDownloadEventsCache[cacheKey] = cachedEvent;
                                        }
                                    }

                                    if (cachedEvent == null || !(cachedEvent.LastUpdatedAt > lanCacheLogLine.DateTime.AddMinutes(-5)))
                                    {
                                        _logger.LogInformation("Adding new event because more than 5 minutes no update: {CacheKey} ({DateTime})", cacheKey, lanCacheLogLine.DateTime);

                                        uint.TryParse(lanCacheLogLine.DownloadIdentifier, out var downloadIdentifierInt);
                                        cachedEvent = new DbDownloadEvent()
                                        {
                                            CacheIdentifier = lanCacheLogLine.CacheIdentifier,
                                            DownloadIdentifierString = lanCacheLogLine.DownloadIdentifier,
                                            DownloadIdentifier = downloadIdentifierInt,
                                            CreatedAt = lanCacheLogLine.DateTime,
                                            LastUpdatedAt = lanCacheLogLine.DateTime,
                                            ClientIp = lanCacheLogLine.RemoteAddress
                                        };
                                        steamAppDownloadEventsCache[cacheKey] = cachedEvent;
                                        await dbContext.DownloadEvents.AddAsync(cachedEvent);
                                    }

                                    cachedEvent.LastUpdatedAt = lanCacheLogLine.DateTime;
                                    if (lanCacheLogLine.UpstreamCacheStatus == "HIT")
                                    {
                                        cachedEvent.CacheHitBytes += lanCacheLogLine.BodyBytesSentLong;
                                    }
                                    else
                                    {
                                        cachedEvent.CacheMissBytes += lanCacheLogLine.BodyBytesSentLong;
                                    }
                                }

                                // Save total bytes read only for the main log file
                                if (Path.GetFileName(logFile) == "access.log")
                                {
                                    var totalByteReadSetting = await dbContext.Settings.FirstOrDefaultAsync(t => t.Key == DbSetting.SettingKey_TotalBytesRead);
                                    if (totalByteReadSetting == null)
                                    {
                                        totalByteReadSetting = new DbSetting()
                                        {
                                            Key = DbSetting.SettingKey_TotalBytesRead
                                        };
                                        await dbContext.Settings.AddAsync(totalByteReadSetting);
                                    }
                                    totalByteReadSetting.Value = TotalBytesRead.ToString();
                                }

                                await dbContext.SaveChangesAsync();
                                FrontendRefresherService.RequireFrontendRefresh();
                            });
                        }
                    }
                }
            }
        }

        public IEnumerable<List<LanCacheLogEntryRaw>> Batch2(IEnumerable<LanCacheLogEntryRaw?> collection, int batchSize, DateTime skipOlderThen)
        {
            int skipCounter = 0;

            int dontLogForSpecificCounter = 0;

            var nextbatch = new List<LanCacheLogEntryRaw>();
            foreach (var logEntry in collection)
            {
                if (logEntry == null)
                {
                    if (nextbatch.Any())
                    {
                        yield return nextbatch;
                        nextbatch = new List<LanCacheLogEntryRaw>();
                        dontLogForSpecificCounter = 0;
                    }
                    else
                    {
                        //Only log once in 30 times
                        if (dontLogForSpecificCounter % 30 == 0)
                        {
                            _logger.LogInformation("{Now} No new log lines, waiting...", DateTime.Now);
                        }
                        dontLogForSpecificCounter++;
                        Thread.Sleep(1000);
                        continue;
                    }
                }
                else if (logEntry.DateTime > skipOlderThen)
                {
                    nextbatch.Add(logEntry);
                    if (nextbatch.Count == batchSize)
                    {
                        yield return nextbatch;
                        nextbatch = new List<LanCacheLogEntryRaw>();
                    }
                }
                else
                {
                    skipCounter++;
                    if (skipCounter % 1000 == 0)
                    {
                        _logger.LogInformation("Skipped total of {SkipCounter} lines (already processed)", skipCounter);
                    }
                }
            }
        }

        static IEnumerable<string> TailFrom(string file, CancellationToken stoppingToken)
        {
            using (var reader = File.OpenText(file))
            {
                // go to end - if the next line is commented out, all the lines from the beginning is returned
                // reader.BaseStream.Seek(0, SeekOrigin.End);
                while (true)
                {
                    stoppingToken.ThrowIfCancellationRequested();

                    string? line = reader.ReadLine();
                    if (reader.BaseStream.Length < reader.BaseStream.Position)
                    {
                        Console.WriteLine($"Uhh: {reader.BaseStream.Length} < {reader.BaseStream.Position}");
                        //reader.BaseStream.Seek(0, SeekOrigin.Begin);

                    }

                    if (line != null)
                    {
                        yield return line;
                    }
                    else
                    {
                        yield return null;
                    }
                }
            }
        }

        public long TotalBytesRead { get; set; }

        /// <summary>
        /// Gets all log files to process based on configuration settings
        /// </summary>
        /// <param name="logDirectory">The directory containing log files</param>
        /// <returns>A list of log file paths to process in order (oldest first)</returns>
        private List<string> GetLogFilesToProcess(string logDirectory)
        {
            var result = new List<string>();
            
            // Always add the main log file first (will be processed last)
            var mainLogFile = Path.Combine(logDirectory, "access.log");
            if (File.Exists(mainLogFile))
            {
                result.Add(mainLogFile);
            }
            
            // Add rotated logs if enabled
            if (_deveLanCacheConfiguration.Feature_ReadRotatedLogs)
            {
                var rotatedLogPattern = Path.Combine(logDirectory, "access.log.*");
                var maxLogsToRead = _deveLanCacheConfiguration.RotatedLogsToRead;
                
                var rotatedFiles = new List<string>();
                
                // Add uncompressed rotated logs
                rotatedFiles.AddRange(Directory.GetFiles(logDirectory, "access.log.*")
                    .Where(f => !f.EndsWith(".gz") && !f.EndsWith(".zst") && int.TryParse(f.Split('.').Last(), out _)));
                
                // Add compressed logs if enabled
                if (_deveLanCacheConfiguration.Feature_ReadCompressedLogs)
                {
                    rotatedFiles.AddRange(Directory.GetFiles(logDirectory, "access.log.*.gz"));
                    rotatedFiles.AddRange(Directory.GetFiles(logDirectory, "access.log.*.zst"));
                }
                
                // Sort rotated logs by rotation number
                rotatedFiles = rotatedFiles
                    .Select(f => new 
                    {
                        Path = f,
                        RotationNumber = GetRotationNumber(f)
                    })
                    .Where(f => f.RotationNumber.HasValue)
                    .OrderByDescending(f => f.RotationNumber.Value)
                    .Take(maxLogsToRead)
                    .Select(f => f.Path)
                    .ToList();
                
                // Add rotated logs in reverse (process oldest first)
                result.AddRange(rotatedFiles);
            }
            
            // Reverse to process oldest logs first, newest last
            result.Reverse();
            
            return result;
        }
        
        /// <summary>
        /// Gets the rotation number from a log file name
        /// </summary>
        private int? GetRotationNumber(string filePath)
        {
            var fileName = Path.GetFileName(filePath);
            var parts = fileName.Split('.');
            
            // Handle uncompressed files (access.log.1)
            if (parts.Length == 3 && int.TryParse(parts[2], out var number))
            {
                return number;
            }
            
            // Handle compressed files (access.log.1.gz or access.log.1.zst)
            if (parts.Length == 4 && int.TryParse(parts[2], out number))
            {
                return number;
            }
            
            return null;
        }
        
        /// <summary>
        /// Creates an appropriate stream for the log file based on its extension
        /// </summary>
        private Stream CreateStreamForLogFile(string filePath)
        {
            try
            {
                var extension = Path.GetExtension(filePath).ToLowerInvariant();
                
                if (extension == ".gz")
                {
                    var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    return new GZipStream(fileStream, CompressionMode.Decompress);
                }
                else if (extension == ".zst")
                {
                    _logger.LogInformation("Opening zstd compressed file: {FilePath}", filePath);
                    try
                    {
                        // Try to read with external command if ZstdSharp is not available
                        var tempFile = Path.GetTempFileName();
                        using (var process = new System.Diagnostics.Process())
                        {
                            process.StartInfo.FileName = "zstd";
                            process.StartInfo.Arguments = $"-d -c \"{filePath}\" > \"{tempFile}\"";
                            process.StartInfo.UseShellExecute = true;
                            process.StartInfo.CreateNoWindow = true;
                            
                            process.Start();
                            process.WaitForExit();
                            
                            if (process.ExitCode == 0)
                            {
                                return new FileStream(tempFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning("Failed to decompress zstd file using external command: {Error}", ex.Message);
                    }
                    
                    // Fallback to direct file read if decompression failed
                    _logger.LogWarning("ZstdSharp not available and zstd command failed. Skipping file: {FilePath}", filePath);
                    return null;
                }
                else
                {
                    // Regular uncompressed file
                    return new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating stream for log file: {FilePath}", filePath);
                return null;
            }
        }

        public IEnumerable<string> TailFrom2(Stream inputStream, CancellationToken stoppingToken)
        {
            if (inputStream.Length >= TotalBytesRead)
            {
                inputStream.Position = TotalBytesRead;
            }
            else
            {
                TotalBytesRead = 0;
            }

            const int BufferSize = 1024;
            var buffer = new byte[BufferSize];
            var leftoverBuffer = new List<byte>();
            int bytesRead;

            while (true)
            {
                stoppingToken.ThrowIfCancellationRequested();

                bytesRead = inputStream.Read(buffer, 0, BufferSize);

                if (bytesRead == 0)
                {
                    yield return null;
                    continue;
                }

                int newlineIndex;
                var searchStartIndex = 0;

                while ((newlineIndex = Array.IndexOf(buffer, (byte)'\n', searchStartIndex, bytesRead - searchStartIndex)) != -1)
                {
                    // Include \r in the line if present                   
                    var hasRAtTheEnd = newlineIndex > 0 ? buffer[newlineIndex - 1] == '\r' : (leftoverBuffer.Count > 0 ? leftoverBuffer[^1] == '\r' : false);
                    var lineEndIndex = hasRAtTheEnd ? newlineIndex - 1 : newlineIndex;

                    var lineBuffer = new byte[leftoverBuffer.Count + lineEndIndex - searchStartIndex];
                    leftoverBuffer.CopyTo(0, lineBuffer, 0, Math.Min(lineBuffer.Length, leftoverBuffer.Count));
                    if (lineEndIndex - searchStartIndex > 0)
                    {
                        Array.Copy(buffer, searchStartIndex, lineBuffer, leftoverBuffer.Count, lineEndIndex - searchStartIndex);
                    }

                    TotalBytesRead += lineBuffer.Length + (hasRAtTheEnd ? 1 : 0) + 1;
                    yield return Encoding.UTF8.GetString(lineBuffer);

                    leftoverBuffer.Clear();
                    searchStartIndex = newlineIndex + 1;
                }

                // Save leftover data for next loop
                leftoverBuffer.AddRange(buffer.Skip(searchStartIndex).Take(bytesRead - searchStartIndex));
            }
        }
    }
}
