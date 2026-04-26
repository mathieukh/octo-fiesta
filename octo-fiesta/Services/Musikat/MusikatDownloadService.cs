using Microsoft.Extensions.Options;
using octo_fiesta.Models.Domain;
using octo_fiesta.Models.Download;
using octo_fiesta.Models.Settings;
using octo_fiesta.Services.Common;
using octo_fiesta.Services.Deezer;
using octo_fiesta.Services.Local;
using IOFile = System.IO.File;

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

    protected override async Task<string> DownloadSongInternalAsync(string externalProvider, string externalId, bool triggerAlbumDownload, bool forcePermanent = false, CancellationToken cancellationToken = default)
    {
        if (externalProvider != ProviderName && externalProvider != "deezer")
        {
            throw new NotSupportedException($"Provider '{externalProvider}' is not supported");
        }

        var songId = $"ext-{externalProvider}-{externalId}";
        // In Cache mode, downloads go to cache unless forcePermanent is set (used by star album/playlist)
        var isCache = SubsonicSettings.StorageMode == StorageMode.Cache && !forcePermanent;

        // Acquire lock BEFORE checking existence to prevent race conditions with concurrent requests
        await DownloadLock.WaitAsync(cancellationToken);

        // Tell other concurrent tasks we are started downloading routine
        // and keep reference to it for easy access
        DownloadInfo ourDownloadInfo = ActiveDownloads[songId] = new()
        {
            SongId = songId,
            ExternalId = externalId,
            ExternalProvider = externalProvider,
            Status = DownloadStatus.InProgress,
            StartedAt = DateTime.UtcNow
        };
        
        try
        {
            // Check if already downloaded (skip for cache mode as we want to check cache folder)
            if (!isCache)
            {
                var existingMapping = await LocalLibraryService.GetMappingForExternalSongAsync(externalProvider, externalId);
                if (existingMapping != null && IOFile.Exists(existingMapping.LocalPath))
                {
                    // Check if we should upgrade quality
                    var targetQuality = GetTargetQuality();
                    bool shouldUpgrade = SubsonicSettings.AutoUpgradeQuality
                        && QualityHelper.ShouldUpgrade(existingMapping.DownloadedQuality, targetQuality);

                    // No upgrade needed – return already downloaded path
                    if (!shouldUpgrade)
                    {
                        Logger.LogInformation("Song already downloaded: {Path}", existingMapping.LocalPath);
                        ourDownloadInfo.Status = DownloadStatus.Completed;
                        ourDownloadInfo.CompletedAt = DateTime.UtcNow;
                        ourDownloadInfo.LocalPath = existingMapping.LocalPath;
                        return existingMapping.LocalPath;
                    }

                    // Back up existing track for quality upgrade
                    Logger.LogInformation("Upgrading quality from {OldQuality} to {NewQuality} for: {Path}",
                        existingMapping.DownloadedQuality ?? "unknown", targetQuality, existingMapping.LocalPath);
                    var backupPath = existingMapping.LocalPath + ".backup";
                    try
                    {
                        IOFile.Move(existingMapping.LocalPath, backupPath);
                    }
                    catch (Exception ex)
                    {
                        Logger.LogWarning(ex, "Failed to create backup for quality upgrade, skipping upgrade");
                        ourDownloadInfo.Status = DownloadStatus.Completed;
                        ourDownloadInfo.CompletedAt = DateTime.UtcNow;
                        ourDownloadInfo.LocalPath = existingMapping.LocalPath;
                        return existingMapping.LocalPath;
                    }

                    // Store backup path to restore on failure
                    ourDownloadInfo.BackupPath = backupPath;
                }
            }
            else
            {
                // For cache mode, check if file exists in cache directory
                var cachedPath = await GetCachedFilePathAsync(externalProvider, externalId, cancellationToken);
                if (cachedPath != null && IOFile.Exists(cachedPath))
                {
                    Logger.LogInformation("Song found in cache: {Path}", cachedPath);
                    // Update file access time for cache cleanup logic
                    IOFile.SetLastAccessTime(cachedPath, DateTime.UtcNow);
                    ourDownloadInfo.Status = DownloadStatus.Completed;
                    ourDownloadInfo.CompletedAt = DateTime.UtcNow;
                    ourDownloadInfo.LocalPath = cachedPath;
                    return cachedPath;
                }
            }

            Song song = await GetSongMetadataForTrackAsync(externalProvider, externalId);
            var downloadResult = await DownloadTrackAsync(externalId, song, cancellationToken);
            string localPath;
            await using (downloadResult.DownloadStream)
            {
                localPath = await SaveDownloadStreamToFileAsync(downloadResult, song, isCache, cancellationToken);
            }
            song.LocalPath = localPath;

            ourDownloadInfo.Status = DownloadStatus.Completed;
            ourDownloadInfo.LocalPath = localPath;
            ourDownloadInfo.CompletedAt = DateTime.UtcNow;

            // Invalidate the metadata path cache so subsequent requests find the newly downloaded file
            var cacheKey = $"{externalProvider}|{externalId}";
            _metadataPathCache.TryRemove(cacheKey, out _);

            // Check if this track belongs to a playlist and update M3U
            if (PlaylistSyncService != null)
            {
                try
                {
                    var playlistId = PlaylistSyncService.GetPlaylistIdForTrack(songId);
                    if (playlistId != null)
                    {
                        Logger.LogInformation("Track {SongId} belongs to playlist {PlaylistId}, adding to M3U", songId, playlistId);
                        await PlaylistSyncService.AddTrackToM3UAsync(playlistId, song, localPath, isFullPlaylistDownload: false);
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogWarning(ex, "Failed to update playlist M3U for track {SongId}", songId);
                }
            }

            // Only register and scan if NOT in cache mode
            if (!isCache)
            {
                await LocalLibraryService.RegisterDownloadedSongAsync(song, localPath, downloadResult.DownloadedQuality);

                // Trigger a Subsonic library rescan (with debounce)
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await LocalLibraryService.TriggerLibraryScanAsync();
                    }
                    catch (Exception ex)
                    {
                        Logger.LogWarning(ex, "Failed to trigger library scan after download");
                    }
                });

                // If download mode is Album and triggering is enabled, start background download of remaining tracks
                if (triggerAlbumDownload && SubsonicSettings.DownloadMode == DownloadMode.Album && !string.IsNullOrEmpty(song.AlbumId))
                {
                    var albumExternalId = ExtractExternalIdFromAlbumId(song.AlbumId);
                    if (!string.IsNullOrEmpty(albumExternalId))
                    {
                        Logger.LogInformation("Download mode is Album, triggering background download for album {AlbumId}", albumExternalId);
                        DownloadRemainingAlbumTracksInBackground(externalProvider, albumExternalId, externalId);
                    }
                }
            }
            else
            {
                Logger.LogInformation("Cache mode: skipping library registration and scan");
            }

            Logger.LogInformation("Download completed: {Path}", localPath);
            return localPath;
        }
        catch (Exception ex)
        {
            ourDownloadInfo.Status = DownloadStatus.Failed;
            ourDownloadInfo.ErrorMessage = ex.Message;

            // Restore backup if quality upgrade failed
            if (!string.IsNullOrEmpty(ourDownloadInfo.BackupPath) && IOFile.Exists(ourDownloadInfo.BackupPath))
            {
                try
                {
                    var originalPath = ourDownloadInfo.BackupPath.Replace(".backup", "");
                    IOFile.Move(ourDownloadInfo.BackupPath, originalPath);
                    Logger.LogInformation("Restored backup after failed quality upgrade: {Path}", originalPath);
                }
                catch (Exception restoreEx)
                {
                    Logger.LogError(restoreEx, "Failed to restore backup file: {BackupPath}", ourDownloadInfo.BackupPath);
                }
            }
            if (ex is OperationCanceledException)
            {
                Logger.LogInformation("Download canceled for {SongId}: {Message}", songId, ex.Message);
            }
            else
            {
                Logger.LogError(ex, "Download failed for {SongId}", songId);
            }
            throw;
        }
        finally
        {
            // Clean up backup file on success
            if (ourDownloadInfo.Status == DownloadStatus.Completed &&
                !string.IsNullOrEmpty(ourDownloadInfo.BackupPath) &&
                IOFile.Exists(ourDownloadInfo.BackupPath))
            {
                try
                {
                    IOFile.Delete(ourDownloadInfo.BackupPath);
                    Logger.LogInformation("Deleted backup after successful quality upgrade");
                }
                catch (Exception deleteEx)
                {
                    Logger.LogWarning(deleteEx, "Failed to delete backup file: {BackupPath}", ourDownloadInfo.BackupPath);
                }
            }

            DownloadLock.Release();
        }
    }

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
