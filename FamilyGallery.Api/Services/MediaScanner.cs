using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using FamilyGallery.Api.Data;
using FamilyGallery.Api.Data.Entities;
using FamilyGallery.Api.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FamilyGallery.Api.Services;

// 파일시스템 순회 결과를 DB 인덱스에 반영. 목록 조회는 이 인덱스만 참조.
public sealed class MediaScanner(
    AppDbContext db,
    MediaMetadataReader metadataReader,
    IOptions<GalleryOptions> galleryOptions,
    ILogger<MediaScanner> logger)
{
    // 촬영일시로 성립하지 않는 값 차단. mvhd 미설정 시의 1904-01-01, 손상된 EXIF 등.
    private static readonly DateTime EarliestPlausibleCapture = new(1990, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    // 대량 투입 중 중단되어도 진행분이 남도록 중간 저장.
    private const int SaveBatchSize = 200;

    public async Task<MediaScanResult> ScanAsync(CancellationToken cancellationToken)
    {
        var root = Path.GetFullPath(galleryOptions.Value.RootPath);

        if (!System.IO.Directory.Exists(root))
        {
            logger.LogError("갤러리 루트를 찾을 수 없어 스캔을 건너뜁니다: {Root}", root);

            return MediaScanResult.Empty;
        }

        var files = new Dictionary<string, FileInfo>(StringComparer.Ordinal);

        foreach (var file in EnumerateMediaFiles(root))
        {
            cancellationToken.ThrowIfCancellationRequested();
            files[ToRelativePath(root, file.FullName)] = file;
        }

        var indexed = await db.MediaItems.ToDictionaryAsync(m => m.RelativePath, StringComparer.Ordinal, cancellationToken);

        // 마운트가 풀리면 bind mount 지점이 빈 디렉터리로 남아 전량 삭제로 보임. 인덱스 전멸 방지.
        if (files.Count == 0 && indexed.Count > 0)
        {
            logger.LogWarning(
                "갤러리 루트에 미디어 파일이 하나도 없습니다. 인덱스 {Count}건을 보존하고 스캔을 건너뜁니다: {Root}",
                indexed.Count,
                root);

            return MediaScanResult.Empty;
        }

        // 삭제 반영을 등록보다 먼저 수행. 파일 이동 시 옛 경로의 행이 해시 중복으로 걸리지 않음.
        var removed = RemoveMissing(indexed, files);

        if (removed > 0)
        {
            await db.SaveChangesAsync(cancellationToken);
        }

        var byContentHash = new Dictionary<string, MediaItem>(StringComparer.Ordinal);

        foreach (var item in indexed.Values)
        {
            byContentHash[item.ContentHash] = item;
        }

        var result = await IndexFilesAsync(files, indexed, byContentHash, cancellationToken);

        await db.SaveChangesAsync(cancellationToken);

        return result with { Removed = removed };
    }

    private int RemoveMissing(Dictionary<string, MediaItem> indexed, Dictionary<string, FileInfo> files)
    {
        var missing = new List<string>();

        foreach (var (relativePath, item) in indexed)
        {
            if (!files.ContainsKey(relativePath))
            {
                db.MediaItems.Remove(item);
                missing.Add(relativePath);
            }
        }

        foreach (var relativePath in missing)
        {
            indexed.Remove(relativePath);
        }

        return missing.Count;
    }

    private async Task<MediaScanResult> IndexFilesAsync(
        Dictionary<string, FileInfo> files,
        Dictionary<string, MediaItem> indexed,
        Dictionary<string, MediaItem> byContentHash,
        CancellationToken cancellationToken)
    {
        var added = 0;
        var updated = 0;
        var skipped = 0;
        var pendingSaves = 0;

        foreach (var (relativePath, file) in files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            indexed.TryGetValue(relativePath, out var existing);

            // 크기와 mtime이 그대로면 내용도 그대로로 간주. 해시 재계산을 위해 파일을 열지 않음.
            if (existing is not null
                && existing.FileSize == file.Length
                && existing.FileModifiedAt == file.LastWriteTimeUtc)
            {
                continue;
            }

            if (!MediaTypeSniffer.TryGetTypeByExtension(file.Extension, out var mediaType))
            {
                continue;
            }

            string contentHash;

            try
            {
                contentHash = await ComputeContentHashAsync(file.FullName, cancellationToken);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 업로드 중이거나 권한이 없는 파일. 다음 주기에 재시도.
                logger.LogWarning(ex, "파일을 읽지 못해 인덱싱을 건너뜁니다: {RelativePath}", relativePath);
                skipped++;
                continue;
            }

            if (byContentHash.TryGetValue(contentHash, out var owner) && owner.RelativePath != relativePath)
            {
                // 내용이 같은 파일은 갤러리에 한 번만 노출. ContentHash 유니크 제약과도 일치.
                logger.LogInformation(
                    "내용이 동일한 파일이 이미 인덱싱되어 있어 건너뜁니다: {RelativePath} (기존: {ExistingPath})",
                    relativePath,
                    owner.RelativePath);

                if (existing is not null)
                {
                    db.MediaItems.Remove(existing);
                    indexed.Remove(relativePath);
                }

                skipped++;
                continue;
            }

            var metadata = metadataReader.Read(file.FullName, mediaType);
            var item = existing ?? new MediaItem
            {
                RelativePath = relativePath,
                OriginalFileName = file.Name,
                ContentHash = contentHash
            };

            if (existing is not null)
            {
                byContentHash.Remove(existing.ContentHash);
            }

            Apply(item, file, mediaType, contentHash, metadata);

            if (existing is null)
            {
                db.MediaItems.Add(item);
                indexed[relativePath] = item;
                added++;
            }
            else
            {
                updated++;
            }

            byContentHash[contentHash] = item;

            if (++pendingSaves >= SaveBatchSize)
            {
                await db.SaveChangesAsync(cancellationToken);
                pendingSaves = 0;
            }
        }

        return new MediaScanResult(added, updated, 0, skipped);
    }

    private static void Apply(
        MediaItem item,
        FileInfo file,
        MediaType mediaType,
        string contentHash,
        MediaMetadata metadata)
    {
        var modifiedAt = file.LastWriteTimeUtc;

        item.OriginalFileName = file.Name;
        item.MediaType = mediaType;
        item.FileSize = file.Length;
        item.ContentHash = contentHash;
        item.FileModifiedAt = modifiedAt;
        item.IndexedAt = DateTime.UtcNow;
        item.Width = metadata.Width;
        item.Height = metadata.Height;
        item.DurationMs = metadata.DurationMs;
        item.CapturedAt = ResolveCapturedAt(metadata.CapturedAt, modifiedAt);
    }

    // 메타데이터에 촬영일시가 없거나 값이 성립하지 않으면 mtime으로 대체.
    private static DateTime ResolveCapturedAt(DateTime? capturedAt, DateTime modifiedAt)
    {
        if (capturedAt is null)
        {
            return modifiedAt;
        }

        var value = capturedAt.Value;

        return value >= EarliestPlausibleCapture && value <= DateTime.UtcNow.AddDays(1)
            ? value
            : modifiedAt;
    }

    private static async Task<string> ComputeContentHashAsync(string filePath, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            bufferSize: 0,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        var hash = await SHA256.HashDataAsync(stream, cancellationToken);

        return Convert.ToHexStringLower(hash);
    }

    private IEnumerable<FileInfo> EnumerateMediaFiles(string root)
    {
        var pending = new Stack<DirectoryInfo>();
        pending.Push(new DirectoryInfo(root));

        while (pending.Count > 0)
        {
            var current = pending.Pop();
            FileSystemInfo[] entries;

            try
            {
                entries = current.GetFileSystemInfos();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(ex, "디렉터리를 읽지 못해 건너뜁니다: {Path}", current.FullName);
                continue;
            }

            foreach (var entry in entries)
            {
                if (entry is DirectoryInfo directory)
                {
                    // 업로드 스테이징(.uploads)과 휴지통(.trash)을 포함한 내부 디렉터리 제외.
                    // 심볼릭 링크는 순회가 순환할 수 있어 따라가지 않음.
                    if (!directory.Name.StartsWith('.') && directory.LinkTarget is null)
                    {
                        pending.Push(directory);
                    }

                    continue;
                }

                if (entry is FileInfo file && MediaTypeSniffer.TryGetTypeByExtension(file.Extension, out _))
                {
                    yield return file;
                }
            }
        }
    }

    private static string ToRelativePath(string root, string fullPath)
    {
        return Path.GetRelativePath(root, fullPath).Replace(Path.DirectorySeparatorChar, '/');
    }
}

public sealed record MediaScanResult(int Added, int Updated, int Removed, int Skipped)
{
    public static readonly MediaScanResult Empty = new(0, 0, 0, 0);
}
