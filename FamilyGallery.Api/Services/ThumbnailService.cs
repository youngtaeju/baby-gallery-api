using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FamilyGallery.Api.Data.Entities;
using FamilyGallery.Api.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FamilyGallery.Api.Services;

// 요청 시점에 생성해 디스크에 캐시.
// 인덱싱 시 미리 만들지 않아 열람하지 않는 미디어는 생성 비용이 없음.
public sealed class ThumbnailService
{

    // 규격 변경 시 캐시와 ETag를 함께 무효화하기 위한 코드 고정 토큰.
    public const string SpecToken = "t512w";

    private const int MaxEdge = 512;

    private const int Quality = 80;

    private const string CacheExtension = ".webp";

    // 첫 프레임이 검은 영상이 흔함. 재생시간이 이보다 길면 앞부분을 건너뜀.
    private const int SeekThresholdMs = 2000;

    private const string SeekSeconds = "1";

    // 확대는 하지 않음. 원본이 규격보다 작으면 그대로 유지.
    // 회전 보정은 지정하지 않음. ffmpeg가 이미지 EXIF orientation과 영상 display matrix를 모두 자동 반영.
    private static readonly string ScaleFilter =
        $"scale='min({MaxEdge},iw)':'min({MaxEdge},ih)':force_original_aspect_ratio=decrease:flags=lanczos";

    // 같은 미디어에 대한 동시 요청을 하나의 생성 작업으로 합침.
    private readonly ConcurrentDictionary<string, Lazy<Task<string?>>> _inFlight = new(StringComparer.Ordinal);

    private readonly SemaphoreSlim _slots;

    private readonly ThumbnailOptions _options;

    private readonly ILogger<ThumbnailService> _logger;

    private readonly string _root;

    private readonly string _temporaryDirectory;

    public ThumbnailService(IOptions<ThumbnailOptions> options, ILogger<ThumbnailService> logger)
    {
        _options = options.Value;
        _logger = logger;
        _slots = new SemaphoreSlim(_options.MaxConcurrency);
        _root = Path.GetFullPath(_options.CachePath);

        // 완성 전 파일을 캐시 트리에 두지 않되 같은 볼륨은 유지. 다른 볼륨이면 rename이 실패함.
        _temporaryDirectory = Path.Combine(_root, ".tmp");
    }

    /// <summary>캐시된 썸네일 경로 반환. 없으면 생성하며, 생성에 실패하면 null.</summary>
    public async Task<string?> GetOrCreateAsync(MediaItem item, string sourcePath, CancellationToken cancellationToken)
    {
        var cachePath = ResolveCachePath(item.ContentHash);

        if (File.Exists(cachePath))
        {
            return cachePath;
        }

        var pending = _inFlight.GetOrAdd(
            item.ContentHash,
            _ => new Lazy<Task<string?>>(
                () => CreateAsync(item, sourcePath, cachePath),
                LazyThreadSafetyMode.ExecutionAndPublication));

        try
        {
            // 호출자가 끊겨도 생성 자체는 계속. 합쳐진 다른 요청이 결과를 받아야 함.
            return await pending.Value.WaitAsync(cancellationToken);
        }
        finally
        {
            _inFlight.TryRemove(new KeyValuePair<string, Lazy<Task<string?>>>(item.ContentHash, pending));
        }
    }

    private async Task<string?> CreateAsync(MediaItem item, string sourcePath, string cachePath)
    {
        // 요청자의 취소 토큰과 분리. 제한 시간만 생성 작업을 중단시킴.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(_options.TimeoutSeconds));

        try
        {
            await _slots.WaitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("썸네일 생성 대기가 제한 시간을 넘었습니다: {RelativePath}", item.RelativePath);

            return null;
        }

        var temporaryPath = Path.Combine(_temporaryDirectory, $"{Guid.NewGuid():N}{CacheExtension}");

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
            Directory.CreateDirectory(_temporaryDirectory);

            if (!await RunAsync(BuildArguments(item, sourcePath, temporaryPath), item.RelativePath, timeout.Token))
            {
                return null;
            }

            // 같은 볼륨이라 rename이 원자적. 생성 중인 파일이 캐시로 노출되지 않음.
            File.Move(temporaryPath, cachePath, overwrite: true);

            return cachePath;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "썸네일을 저장하지 못했습니다: {RelativePath}", item.RelativePath);

            return null;
        }
        finally
        {
            TryDeleteTemporary(temporaryPath);
            _slots.Release();
        }
    }

    private List<string> BuildArguments(MediaItem item, string sourcePath, string outputPath)
    {
        var arguments = new List<string> { "-nostdin", "-v", "error", "-y" };

        // 입력 앞에 두어야 디코딩 없이 건너뜀.
        if (item.MediaType == MediaType.Video && item.DurationMs >= SeekThresholdMs)
        {
            arguments.Add("-ss");
            arguments.Add(SeekSeconds);
        }

        arguments.Add("-i");
        arguments.Add(sourcePath);
        arguments.Add("-frames:v");
        arguments.Add("1");

        // 원본 메타데이터를 썸네일에 싣지 않음. GPS 좌표 전파 차단.
        arguments.Add("-map_metadata");
        arguments.Add("-1");

        arguments.Add("-vf");
        arguments.Add(ScaleFilter);

        arguments.Add("-c:v");
        arguments.Add("libwebp");
        arguments.Add("-quality");
        arguments.Add(Quality.ToString(CultureInfo.InvariantCulture));
        arguments.Add(outputPath);

        return arguments;
    }

    private async Task<bool> RunAsync(List<string> arguments, string relativePath, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(_options.FfmpegPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };

        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            _logger.LogError(ex, "ffmpeg를 실행하지 못했습니다: {FfmpegPath}", _options.FfmpegPath);

            return false;
        }

        // 파이프 버퍼가 차면 프로세스가 멈춤. 종료를 기다리기 전에 읽기를 시작.
        var error = process.StandardError.ReadToEndAsync(cancellationToken);
        var output = process.StandardOutput.ReadToEndAsync(cancellationToken);

        try
        {
            await process.WaitForExitAsync(cancellationToken);
            await Task.WhenAll(error, output);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            _logger.LogWarning("썸네일 생성이 제한 시간을 넘겨 중단했습니다: {RelativePath}", relativePath);

            return false;
        }

        if (process.ExitCode == 0)
        {
            return true;
        }

        _logger.LogWarning(
            "ffmpeg가 종료 코드 {ExitCode}로 실패했습니다: {RelativePath} {Error}",
            process.ExitCode,
            relativePath,
            error.Result);

        return false;
    }

    // 한 디렉터리에 파일이 몰리지 않도록 해시 앞 2자로 분산.
    private string ResolveCachePath(string contentHash)
    {
        return Path.Combine(_root, contentHash[..2], contentHash + CacheExtension);
    }

    private static void TryKill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
        {
            // 이미 종료된 프로세스.
        }
    }

    private void TryDeleteTemporary(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "임시 썸네일 파일을 지우지 못했습니다: {Path}", path);
        }
    }
}
