using Microsoft.Extensions.Options;
using octo_fiesta.Models.Domain;
using octo_fiesta.Models.Settings;
using octo_fiesta.Services.Common;
using octo_fiesta.Services.Deezer;
using octo_fiesta.Services.Local;

namespace octo_fiesta.Services.Musikat;

/// <summary>
/// Download service implementation for Musikat
/// </summary>
public class MusikatDownloadService : BaseDownloadService
{

    private const int MaxRetries = 5;

    private readonly HttpClient _httpClient;

    private readonly string? _url;
    protected override string ProviderName => "musikat";

    public MusikatDownloadService(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILocalLibraryService localLibraryService,
        IMusicMetadataService metadataService,
        IOptions<SubsonicSettings> subsonicSettings,
        IOptions<MusikatSettings> musikatSettings,
        IServiceProvider serviceProvider,
        ILogger<MusikatDownloadService> logger)
        : base(httpClientFactory, configuration, localLibraryService, metadataService, subsonicSettings.Value, serviceProvider, logger)
    {
        _httpClient = httpClientFactory.CreateClient();
        
        var musikatConfig = musikatSettings.Value;
        _url = musikatConfig.Url;
    }

    protected override string? GetTargetQuality() => "320";

    public override Task<bool> IsAvailableAsync()
    {
         if (string.IsNullOrEmpty(_url))
        {
            Logger.LogWarning("Musikat URL not configured");
            return Task.FromResult(false);
        }
        if(!(MetadataService is DeezerMetadataService))
        {
            Logger.LogWarning("Musikat requires Deezer metadata service");
            return Task.FromResult(false);
        }
        return Task.FromResult(true);
    }

    record YTCandidate(string video_id);

    record ApiCandidateResponse(List<YTCandidate> candidates);
    record DownloadStatusResponse(string status, string file_path);

    protected override async Task<DownloadResult> DownloadTrackAsync(string trackId, Song song, CancellationToken cancellationToken)
    {
        var candidatesResponse = await _httpClient.GetFromJsonAsync<ApiCandidateResponse>($"{_url}/api/youtube/candidates/{song.ExternalId}?provider=deezer", cancellationToken);
        if (candidatesResponse == null || candidatesResponse.candidates.Count == 0)        {
            Logger.LogWarning("No YouTube candidates found for track {TrackId}", trackId);
            throw new Exception("No YouTube candidates found");
        }
        var firstCandidate = candidatesResponse.candidates[0];
        await _httpClient.PostAsJsonAsync($"{_url}/api/download", new 
        {
            track_id = song.ExternalId,
            location = "navidrome",
            firstCandidate.video_id,
            format = "mp3",
            quality = GetTargetQuality(),
            provider = "deezer",
            max_retries = 0,
            navidrome_library = "/music"
        }, cancellationToken);

        var waitForDownloadUrl = $"{_url}/api/download/status/{song.ExternalId}";
        var retries = 0;
        do
        {
            await Task.Delay(2000, cancellationToken);
            var statusResponse = await _httpClient.GetFromJsonAsync<DownloadStatusResponse>(waitForDownloadUrl, cancellationToken);
            if (statusResponse == null)
            {
                Logger.LogWarning("Failed to get download status for track {TrackId}", trackId);
                continue;
            }
            if (statusResponse.status == "completed" && !string.IsNullOrEmpty(statusResponse.file_path))
            {
                Logger.LogInformation("Track {TrackId} downloaded successfully to {FilePath}", trackId, statusResponse.file_path);
                var stream = new FileStream(statusResponse.file_path, FileMode.Open, FileAccess.Read, FileShare.Read);
                if(stream == null)
                {
                    Logger.LogWarning("Failed to open downloaded file for track {TrackId}", trackId);
                    throw new Exception("Failed to open downloaded file");
                }
                return new DownloadResult(stream, ".mp3", GetTargetQuality());
            }
            retries++;
        } while (!cancellationToken.IsCancellationRequested && retries < MaxRetries);
        throw new Exception($"Failed to verify downloading of track {trackId} after {MaxRetries} retries");
    }

    protected override string? ExtractExternalIdFromAlbumId(string albumId)
    {
        const string prefix = "ext-deezer-album-";
        if (albumId.StartsWith(prefix))
        {
            return albumId[prefix.Length..];
        }
        return null;
    }
   
}
