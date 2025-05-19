namespace DeveLanCacheUI_Backend.Controllers
{
    [ApiController]
    [Route("[controller]/[action]")]
    public class EpicManifestController : ControllerBase
    {
        private readonly DeveLanCacheUIDbContext _dbContext;
        private readonly EpicManifestService _epicManifestService;
        private readonly ILogger<EpicManifestController> _logger;

        public EpicManifestController(DeveLanCacheUIDbContext dbContext, EpicManifestService epicManifestService, ILogger<EpicManifestController> logger)
        {
            _dbContext = dbContext;
            _epicManifestService = epicManifestService;
            _logger = logger;
        }

        [HttpGet]
        public async Task<IEnumerable<EpicManifest>> GetEpicManifests(int skip = 0, int take = 10)
        {
            var epicManifests = await _dbContext.EpicManifests
                .OrderByDescending(t => t.CreationTime)
                .Skip(skip)
                .Take(take)
                .ToListAsync();

            var mapped = epicManifests
                .Select(t =>
                {
                    var originalProtoBytes = _epicManifestService.GetBytesForUniqueManifestIdentifier(t.UniqueManifestIdentifier);
                    var jsonifiedProtoBytes = originalProtoBytes != null ? EpicManifestService.ManifestBytesToJsonValue(originalProtoBytes) : null;

                    var retval = new EpicManifest()
                    {
                        GameName = t.GameName,
                        AppName = t.AppName,
                        VersionString = t.VersionString,
                        CreationTime = t.CreationTime,
                        UniqueManifestIdentifier = t.UniqueManifestIdentifier,
                        ManifestBytesSize = t.ManifestBytesSize,
                        ProtobufDataAsJson = jsonifiedProtoBytes
                    };
                    return retval;
                }).ToList();
            return mapped;
        }

        [HttpGet]
        public async Task<IEnumerable<EpicManifest>> GetEpicManifestsForGame(string gameName, int skip = 0, int take = 10)
        {
            var epicManifests = await _dbContext.EpicManifests
                .Where(t => t.GameName == gameName)
                .OrderByDescending(t => t.CreationTime)
                .Skip(skip)
                .Take(take)
                .ToListAsync();

            var mapped = epicManifests
                .Select(t =>
                {
                    var originalProtoBytes = _epicManifestService.GetBytesForUniqueManifestIdentifier(t.UniqueManifestIdentifier);
                    var jsonifiedProtoBytes = originalProtoBytes != null ? EpicManifestService.ManifestBytesToJsonValue(originalProtoBytes) : null;

                    var retval = new EpicManifest()
                    {
                        GameName = t.GameName,
                        AppName = t.AppName,
                        VersionString = t.VersionString,
                        CreationTime = t.CreationTime,
                        UniqueManifestIdentifier = t.UniqueManifestIdentifier,
                        ManifestBytesSize = t.ManifestBytesSize,
                        ProtobufDataAsJson = jsonifiedProtoBytes
                    };
                    return retval;
                }).ToList();
            return mapped;
        }
    }
}