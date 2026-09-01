using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BabyGallery.Api.Data;
using BabyGallery.Api.Data.Entities;
using BabyGallery.Api.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BabyGallery.Api.Services;

// 삭제 요청은 휴지통 이동으로 처리하고, 파일시스템 실삭제는 보존 기간 경과 후 수행.
// 인덱스 제거로 동일 파일의 재업로드를 허용하며, 삭제 이력은 감사 로그로 보존.
public sealed class MediaTrashService(
    AppDbContext db,
    ThumbnailService thumbnails,
    GalleryTimeZone timeZone,
    IOptions<GalleryOptions> galleryOptions,
    IOptions<UploadOptions> uploadOptions,
    ILogger<MediaTrashService> logger)
{
    // 무한 탐색 방지용 상한.
    private const int MaxNameAttempts = 100;

    /// <summary>원본을 휴지통으로 옮기고 인덱스에서 제거. 파일을 옮기지 못하면 false.</summary>
    public async Task<bool> MoveToTrashAsync(
        MediaItem item,
        int userId,
        string username,
        CancellationToken cancellationToken)
    {
        var gallery = galleryOptions.Value;

        if (!gallery.TryResolveMediaPath(item.RelativePath, out var sourcePath))
        {
            logger.LogError("갤러리 루트 밖을 가리키는 경로입니다: {RelativePath}", item.RelativePath);

            return false;
        }

        var trashRoot = uploadOptions.Value.ResolveTrashPath(gallery.RootPath);

        if (!TryResolveTrashPath(trashRoot, item.RelativePath, out var targetPath))
        {
            logger.LogError("휴지통 대상 파일명을 정하지 못했습니다: {RelativePath}", item.RelativePath);

            return false;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);

            File.Move(sourcePath, targetPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogError(ex, "휴지통으로 옮기지 못했습니다: {RelativePath}", item.RelativePath);

            return false;
        }

        db.MediaDeletions.Add(new MediaDeletion
        {
            ContentHash = item.ContentHash,
            OriginalRelativePath = item.RelativePath,
            TrashRelativePath = ToRelativePath(trashRoot, targetPath),
            OriginalFileName = item.OriginalFileName,
            FileSize = item.FileSize,
            DeletedByUserId = userId,
            DeletedByUsername = username,
            DeletedAt = DateTime.UtcNow
        });

        db.MediaItems.Remove(item);

        await db.SaveChangesAsync(cancellationToken);

        thumbnails.DeleteCached(item.ContentHash);

        return true;
    }

    /// <summary>보존 기간이 지난 휴지통 항목을 실삭제. 처리한 건수 반환.</summary>
    public async Task<int> PurgeExpiredAsync(CancellationToken cancellationToken)
    {
        var options = uploadOptions.Value;
        var cutoff = DateTime.UtcNow.AddDays(-options.TrashRetentionDays);

        // 디렉터리 순회가 아닌 감사 로그 기준. 날짜 폴더명은 현지시각이라 DeletedAt과 하루 어긋날 수 있음.
        var expired = await db.MediaDeletions
            .Where(d => d.PurgedAt == null && d.DeletedAt < cutoff)
            .ToListAsync(cancellationToken);

        if (expired.Count == 0)
        {
            return 0;
        }

        var trashRoot = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(options.ResolveTrashPath(galleryOptions.Value.RootPath)));

        var purgedAt = DateTime.UtcNow;
        var emptied = new HashSet<string>(StringComparer.Ordinal);
        var purged = 0;

        foreach (var deletion in expired)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!GalleryOptions.TryResolveUnder(trashRoot, deletion.TrashRelativePath, out var fullPath))
            {
                // 잘못된 휴지통 경로를 삭제 완료로 기록하지 않고 재처리 대상으로 유지.
                // 반복 오류 로그를 통한 데이터 이상 감지.
                logger.LogError(
                    "휴지통 루트 밖을 가리키는 기록입니다: {TrashRelativePath}",
                    deletion.TrashRelativePath);

                continue;
            }

            if (!TryDeleteFile(fullPath))
            {
                continue;
            }

            emptied.Add(Path.GetDirectoryName(fullPath)!);
            deletion.PurgedAt = purgedAt;
            purged++;
        }

        await db.SaveChangesAsync(cancellationToken);

        RemoveEmptyDirectories(trashRoot, emptied);

        logger.LogInformation("휴지통 정리 완료. {Purged}건 실삭제.", purged);

        return purged;
    }

    private bool TryDeleteFile(string fullPath)
    {
        if (!File.Exists(fullPath))
        {
            return true;
        }

        try
        {
            File.Delete(fullPath);

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "휴지통 파일을 지우지 못했습니다: {FullName}", fullPath);

            return false;
        }
    }

    private void RemoveEmptyDirectories(string trashRoot, HashSet<string> directories)
    {
        foreach (var directory in directories)
        {
            var current = directory;

            // 상위 날짜 폴더까지 순차 정리하고 휴지통 루트는 유지.
            while (current.StartsWith(trashRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                try
                {
                    if (Directory.EnumerateFileSystemEntries(current).Any())
                    {
                        break;
                    }

                    Directory.Delete(current);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    logger.LogWarning(ex, "빈 휴지통 디렉터리를 지우지 못했습니다: {Path}", current);

                    break;
                }

                current = Path.GetDirectoryName(current)!;
            }
        }
    }

    private bool TryResolveTrashPath(string trashRoot, string relativePath, out string targetPath)
    {
        var folder = timeZone.ToLocal(DateTime.UtcNow).ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        var basePath = Path.GetFullPath(Path.Combine(trashRoot, folder, relativePath));

        targetPath = basePath;

        var directory = Path.GetDirectoryName(basePath)!;
        var baseName = Path.GetFileNameWithoutExtension(basePath);
        var extension = Path.GetExtension(basePath);

        // 동일 날짜, 경로의 재삭제 시 기존 휴지통 파일 덮어쓰기 방지.
        for (var attempt = 2; File.Exists(targetPath); attempt++)
        {
            if (attempt > MaxNameAttempts)
            {
                return false;
            }

            targetPath = Path.Combine(
                directory,
                $"{baseName}_{attempt.ToString(CultureInfo.InvariantCulture)}{extension}");
        }

        return true;
    }

    private static string ToRelativePath(string root, string fullPath)
    {
        return Path.GetRelativePath(root, fullPath).Replace(Path.DirectorySeparatorChar, '/');
    }
}
