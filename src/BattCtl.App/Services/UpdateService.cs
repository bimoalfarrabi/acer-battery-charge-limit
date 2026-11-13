using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using BattCtl.App.ViewModels;
using Microsoft.UI.Xaml;

namespace BattCtl.App.Services;

public enum UpdateState
{
    Idle,
    Checking,
    UpToDate,
    Available,
    Downloading,
    Ready,
    Installing,
    Error,
    Offline,
    RateLimited
}

/// <summary>
/// Owns the in-app update flow: check GitHub Releases, download the installer,
/// verify its SHA-256, and hand off to an elevated Inno Setup run. Deliberately
/// not on MainViewModel, which re-raises every property on its 5s poll.
///
/// SHA-256 proves integrity, not authenticity. The authenticity anchor is the
/// TLS connection to github.com plus the release itself; there is no
/// "install anyway" bypass on a hash mismatch.
/// </summary>
public class UpdateService : ObservableObject
{
    private const string LatestReleaseUrl =
        "https://api.github.com/repos/bimoalfarrabi/acer-battery-charge-limit/releases/latest";

    private static readonly TimeSpan CacheLifetime = TimeSpan.FromHours(6);

    private static readonly string DataDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BattCtl.App");

    private static readonly string CachePath = Path.Combine(DataDirectory, "update-cache.json");
    private static readonly string SettingsPath = Path.Combine(DataDirectory, "update-settings.json");
    private static readonly string UpdatesDirectory = Path.Combine(DataDirectory, "updates");

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private readonly JsonSerializerOptions _json = new() { WriteIndented = true };
    private readonly SemaphoreSlim _downloadGate = new(1, 1);

    private UpdateState _state = UpdateState.Idle;
    private string? _latestVersion;
    private string? _releaseNotes;
    private string? _downloadUrl;
    private long? _assetSize;
    private string? _expectedDigest;
    private double _downloadProgress;
    private string? _message;
    private bool _messageIsError;
    private DateTimeOffset? _lastChecked;
    private bool _autoCheckEnabled = true;
    private string? _downloadedInstallerPath;
    private CancellationTokenSource? _downloadCancellation;

    public UpdateService()
    {
        LoadSettings();
        LoadCache();
    }

    public UpdateState State
    {
        get => _state;
        private set
        {
            if (SetProperty(ref _state, value))
            {
                // The button/status line/InfoBar all key off the state; a
                // coarse refresh keeps the code-behind simple.
                RaiseAllPropertiesChanged();
            }
        }
    }

    public string CurrentVersion => AppInfo.Version;

    public string? LatestVersion
    {
        get => _latestVersion;
        private set => SetProperty(ref _latestVersion, value);
    }

    public string? ReleaseNotes
    {
        get => _releaseNotes;
        private set => SetProperty(ref _releaseNotes, value);
    }

    public double DownloadProgress
    {
        get => _downloadProgress;
        private set => SetProperty(ref _downloadProgress, value);
    }

    public string? Message
    {
        get => _message;
        private set => SetProperty(ref _message, value);
    }

    public bool MessageIsError
    {
        get => _messageIsError;
        private set => SetProperty(ref _messageIsError, value);
    }

    public DateTimeOffset? LastChecked
    {
        get => _lastChecked;
        private set => SetProperty(ref _lastChecked, value);
    }

    public bool AutoCheckEnabled
    {
        get => _autoCheckEnabled;
        set
        {
            if (SetProperty(ref _autoCheckEnabled, value))
            {
                PersistSettings();
            }
        }
    }

    public bool IsBusy => State is UpdateState.Checking or UpdateState.Downloading or UpdateState.Installing;

    public bool IsChecking => State is UpdateState.Checking;

    public bool CanCheck => State is UpdateState.Idle or UpdateState.UpToDate
        or UpdateState.Available or UpdateState.Error or UpdateState.Offline or UpdateState.RateLimited;

    public bool CanDownload => (State is UpdateState.Available or UpdateState.Ready)
        && !string.IsNullOrEmpty(_downloadUrl);

    public bool CanInstall => State is UpdateState.Ready && _downloadedInstallerPath is not null;

    public bool CanCancel => State is UpdateState.Downloading;

    public bool IsUpdateAvailable => State is UpdateState.Available or UpdateState.Downloading
        or UpdateState.Ready or UpdateState.Installing;

    public async Task CheckForUpdatesAsync(bool manual, CancellationToken cancellationToken)
    {
        // One attempt per launch: an automatic check that already ran (any
        // outcome) is not repeated by startup code re-entering.
        if (!manual && State is not (UpdateState.Idle or UpdateState.Error or UpdateState.Offline or UpdateState.RateLimited))
        {
            return;
        }

        if (!CanCheck)
        {
            return;
        }

        State = UpdateState.Checking;
        try
        {
            var release = await FetchLatestReleaseAsync(cancellationToken).ConfigureAwait(true);
            if (release is null)
            {
                return;
            }

            ApplyRelease(release);
            if (_downloadUrl is null)
            {
                // A newer tag with no installer asset is not actionable. A manual
                // check says so; an automatic one stays quiet.
                State = manual ? UpdateState.Error : UpdateState.UpToDate;
                if (manual)
                {
                    Message = "The release has no installer asset.";
                    MessageIsError = true;
                }
            }
            else
            {
                State = IsNewerVersion(release.TagName, AppInfo.SemanticVersion)
                    ? UpdateState.Available
                    : UpdateState.UpToDate;
            }

            PersistCache();
        }
        catch (RateLimitException)
        {
            if (manual)
            {
                State = UpdateState.RateLimited;
                Message = "GitHub is rate-limiting checks. Try again later.";
                MessageIsError = false;
            }
            else
            {
                // Silent: leave the card in its previous state, not an error.
                State = UpdateState.Idle;
            }
        }
        catch (HttpRequestException)
        {
            if (manual)
            {
                State = UpdateState.Error;
                Message = "Couldn't check for updates.";
                MessageIsError = true;
            }
            else
            {
                State = UpdateState.Offline;
            }
        }
        catch (OperationCanceledException)
        {
            State = UpdateState.Idle;
        }
        catch (Exception exception)
        {
            if (manual)
            {
                State = UpdateState.Error;
                Message = $"Couldn't check for updates: {exception.Message}";
                MessageIsError = true;
            }
            else
            {
                State = UpdateState.Idle;
            }
        }
    }

    public async Task DownloadAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        if (!CanDownload || _downloadUrl is null || LatestVersion is null)
        {
            return;
        }

        // Serialize attempts: a second click while the first is still unwinding
        // must not race the same .part file.
        await _downloadGate.WaitAsync(cancellationToken).ConfigureAwait(true);
        try
        {
            // A verified installer from an earlier run is reused; retry skips the
            // download.
            var targetPath = InstallerPath(LatestVersion);
            if (File.Exists(targetPath))
            {
                if (await VerifyInstallerAsync(targetPath).ConfigureAwait(true))
                {
                    _downloadedInstallerPath = targetPath;
                    State = UpdateState.Ready;
                    return;
                }
            }

            Directory.CreateDirectory(UpdatesDirectory);
            CleanupStaleParts();

            // A unique .part name means a stale file locked by a previous attempt
            // (or by an antivirus scan) can never block this download.
            var partPath = targetPath + "." + Guid.NewGuid().ToString("N") + ".part";
            _downloadCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            State = UpdateState.Downloading;
            DownloadProgress = 0;

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, _downloadUrl);
                request.Headers.UserAgent.ParseAdd($"BattCtl/{AppInfo.Version}");
                using var response = await _http.SendAsync(
                    request, HttpCompletionOption.ResponseHeadersRead, _downloadCancellation.Token).ConfigureAwait(true);
                response.EnsureSuccessStatusCode();

                var total = response.Content.Headers.ContentLength ?? _assetSize ?? -1;
                await using (var source = await response.Content.ReadAsStreamAsync(_downloadCancellation.Token).ConfigureAwait(true))
                await using (var target = File.Create(partPath))
                {
                    var buffer = new byte[81920];
                    long written = 0;
                    int read;
                    while ((read = await source.ReadAsync(buffer, _downloadCancellation.Token).ConfigureAwait(true)) > 0)
                    {
                        await target.WriteAsync(buffer.AsMemory(0, read), _downloadCancellation.Token).ConfigureAwait(true);
                        written += read;
                        var percent = total > 0 ? Math.Min(100.0, written * 100.0 / total) : 0;
                        DownloadProgress = percent;
                        if (total > 0)
                        {
                            progress.Report(percent);
                        }
                    }

                    await target.FlushAsync(_downloadCancellation.Token).ConfigureAwait(true);
                }

                if (!await VerifyFileHashAsync(partPath).ConfigureAwait(true))
                {
                    TryDelete(partPath);
                    State = UpdateState.Error;
                    Message = "The downloaded installer failed its SHA-256 check and was deleted.";
                    MessageIsError = true;
                    return;
                }

                File.Move(partPath, targetPath, overwrite: true);
                _downloadedInstallerPath = targetPath;
                DownloadProgress = 100;
                State = UpdateState.Ready;
            }
            catch (OperationCanceledException)
            {
                TryDelete(partPath);
                State = UpdateState.Available;
            }
            catch (Exception exception)
            {
                TryDelete(partPath);
                State = UpdateState.Error;
                Message = $"Couldn't download the update: {exception.Message}";
                MessageIsError = true;
            }
            finally
            {
                _downloadCancellation?.Dispose();
                _downloadCancellation = null;
            }
        }
        finally
        {
            _downloadGate.Release();
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private void CleanupStaleParts()
    {
        try
        {
            foreach (var stale in Directory.EnumerateFiles(UpdatesDirectory, "*.part"))
            {
                TryDelete(stale);
            }
        }
        catch (DirectoryNotFoundException)
        {
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    public async Task InstallAsync()
    {
        if (!CanInstall || _downloadedInstallerPath is null)
        {
            return;
        }

        State = UpdateState.Installing;
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = _downloadedInstallerPath,
                UseShellExecute = true,
                Verb = "runas",
                Arguments = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP-"
            };
            Process.Start(startInfo);

            // The installer needs the mutex released to proceed; quit so it can
            // replace files and relaunch the app.
            (Application.Current as App)?.Quit();
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == 1223)
        {
            // UAC cancel: keep the downloaded file, a retry skips the download.
            State = UpdateState.Ready;
            Message = "Installation was cancelled. The downloaded installer is kept.";
            MessageIsError = false;
        }
        catch (Exception exception)
        {
            State = UpdateState.Ready;
            Message = $"Couldn't start the installer: {exception.Message}";
            MessageIsError = true;
        }
    }

    public void CancelDownload() => _downloadCancellation?.Cancel();

    private sealed record CachedRelease(
        [property: JsonPropertyName("tag_name")] string TagName,
        [property: JsonPropertyName("body")] string? Body,
        [property: JsonPropertyName("checked_at")] DateTimeOffset CheckedAt);

    private sealed record UpdateSettings(
        [property: JsonPropertyName("auto_check_enabled")] bool AutoCheckEnabled);

    private sealed class RateLimitException : Exception
    {
        public RateLimitException() : base("GitHub API rate limit reached.")
        {
        }
    }

    private sealed record GitHubAsset(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("browser_download_url")] string BrowserDownloadUrl,
        [property: JsonPropertyName("size")] long Size,
        [property: JsonPropertyName("digest")] string? Digest);

    private sealed record GitHubRelease(
        [property: JsonPropertyName("tag_name")] string TagName,
        [property: JsonPropertyName("body")] string? Body,
        [property: JsonPropertyName("assets")] GitHubAsset[] Assets);

    private async Task<GitHubRelease?> FetchLatestReleaseAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseUrl);
        request.Headers.UserAgent.ParseAdd($"BattCtl/{AppInfo.Version}");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");

        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(true);
        if ((int)response.StatusCode == 403
            && response.Headers.TryGetValues("X-RateLimit-Remaining", out var remaining)
            && remaining.FirstOrDefault() == "0")
        {
            throw new RateLimitException();
        }

        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(true);
        var release = await JsonSerializer.DeserializeAsync<GitHubRelease>(stream, cancellationToken: cancellationToken)
            .ConfigureAwait(true) ?? throw new InvalidOperationException("The release payload was null.");

        LastChecked = DateTimeOffset.Now;
        return release;
    }

    private void ApplyRelease(GitHubRelease release)
    {
        _checksumAssetUrl = null;
        LatestVersion = release.TagName.StartsWith('v') ? release.TagName[1..] : release.TagName;
        ReleaseNotes = string.IsNullOrWhiteSpace(release.Body) ? null : release.Body.Trim();

        var asset = release.Assets.FirstOrDefault(asset =>
            asset.Name.StartsWith("BattCtl-Setup-", StringComparison.OrdinalIgnoreCase)
            && asset.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));

        _downloadUrl = asset?.BrowserDownloadUrl;
        _assetSize = asset?.Size;
        _expectedDigest = asset?.Digest;

        // Fall back to a SHA256SUMS.txt asset when the per-asset digest is
        // missing (the digest field is newer than some GitHub tooling).
        if (_expectedDigest is null)
        {
            var sums = release.Assets.FirstOrDefault(asset =>
                asset.Name.Equals("SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase));
            if (sums is not null)
            {
                _ = sums; // fetched and parsed during verification, not here
                _expectedDigest = null;
                _checksumAssetUrl = sums.BrowserDownloadUrl;
            }
        }
    }

    private string? _checksumAssetUrl;

    private static bool IsNewerVersion(string tagName, Version? current)
    {
        var candidate = tagName.StartsWith('v') ? tagName[1..] : tagName;
        candidate = candidate.Split('+', 2)[0];
        if (!Version.TryParse(candidate.Split('-', 2)[0], out var parsed))
        {
            return false;
        }

        if (current is null)
        {
            return false;
        }

        return parsed > current;
    }

    private static string InstallerPath(string version) =>
        Path.Combine(UpdatesDirectory, $"BattCtl-Setup-{version}.exe");

    private async Task<bool> VerifyInstallerAsync(string path)
    {
        if (_expectedDigest is not null || _checksumAssetUrl is not null)
        {
            return await VerifyFileHashAsync(path).ConfigureAwait(true);
        }

        // No reference digest at all: refuse silently rather than install
        // something unverified. A fresh check repopulates the digest.
        return false;
    }

    private async Task<bool> VerifyFileHashAsync(string path)
    {
        string? expected = _expectedDigest;
        if (expected is null && _checksumAssetUrl is not null)
        {
            expected = await FetchDigestFromChecksumsAsync(path).ConfigureAwait(true);
            if (expected is null)
            {
                return false;
            }
        }

        if (expected is null)
        {
            return false;
        }

        var prefix = "sha256:";
        if (expected.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            expected = expected[prefix.Length..];
        }

        if (expected.Length != 64 || !IsHexString(expected))
        {
            return false;
        }

        var expectedBytes = Convert.FromHexString(expected);
        var actualBytes = await Task.Run(() =>
        {
            using var stream = File.OpenRead(path);
            return SHA256.HashData(stream);
        }).ConfigureAwait(true);
        return CryptographicOperations.FixedTimeEquals(expectedBytes, actualBytes);
    }

    private async Task<string?> FetchDigestFromChecksumsAsync(string path)
    {
        if (_checksumAssetUrl is null)
        {
            return null;
        }

        var fileName = Path.GetFileName(path);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, _checksumAssetUrl);
            request.Headers.UserAgent.ParseAdd($"BattCtl/{AppInfo.Version}");
            using var response = await _http.SendAsync(request, CancellationToken.None).ConfigureAwait(true);
            response.EnsureSuccessStatusCode();
            var content = await response.Content.ReadAsStringAsync(CancellationToken.None).ConfigureAwait(true);
            foreach (var line in content.Split('\n'))
            {
                var parts = line.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 2
                    && parts[1].TrimStart('*', ' ').Equals(fileName, StringComparison.OrdinalIgnoreCase))
                {
                    return parts[0];
                }
            }
        }
        catch (HttpRequestException)
        {
            return null;
        }

        return null;
    }

    private static bool IsHexString(string value)
    {
        foreach (var character in value)
        {
            if (!Uri.IsHexDigit(character))
            {
                return false;
            }
        }

        return true;
    }

    private void PersistCache()
    {
        try
        {
            Directory.CreateDirectory(DataDirectory);
            var record = new CachedRelease(TagName: LatestVersion ?? string.Empty, Body: ReleaseNotes, CheckedAt: LastChecked ?? DateTimeOffset.Now);
            File.WriteAllText(CachePath, JsonSerializer.Serialize(record, _json));
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private void LoadCache()
    {
        try
        {
            if (!File.Exists(CachePath))
            {
                return;
            }

            var record = JsonSerializer.Deserialize<CachedRelease>(File.ReadAllText(CachePath));
            if (record is null || DateTimeOffset.Now - record.CheckedAt > CacheLifetime)
            {
                return;
            }

            LatestVersion = string.IsNullOrEmpty(record.TagName) ? null : record.TagName;
            ReleaseNotes = record.Body;
            LastChecked = record.CheckedAt;

            // A fresh cache paints the card immediately; the download URL is
            // re-resolved by a fresh check before downloading.
            if (!string.IsNullOrEmpty(record.TagName))
            {
                State = IsNewerVersion(record.TagName, AppInfo.SemanticVersion)
                    ? UpdateState.Available
                    : UpdateState.UpToDate;
            }
        }
        catch (JsonException)
        {
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private void PersistSettings()
    {
        try
        {
            Directory.CreateDirectory(DataDirectory);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(new UpdateSettings(AutoCheckEnabled), _json));
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private void LoadSettings()
    {
        try
        {
            if (!File.Exists(SettingsPath))
            {
                return;
            }

            var settings = JsonSerializer.Deserialize<UpdateSettings>(File.ReadAllText(SettingsPath));
            if (settings is not null)
            {
                _autoCheckEnabled = settings.AutoCheckEnabled;
            }
        }
        catch (JsonException)
        {
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
