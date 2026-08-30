using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FamilyGallery.Api.Data;
using FamilyGallery.Api.Data.Entities;
using FamilyGallery.Api.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FamilyGallery.Api.Services;

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

    private bool TryResolveTrashPath(string trashRoot, string relativePath, out string targetPath)
    {
        var folder = timeZone.ToLocal(DateTime.UtcNow).ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        var basePath = Path.GetFullPath(Path.Combine(trashRoot, folder, relativePath));

        targetPath = basePath;

        var directory = Path.GetDirectoryName(basePath)!;
        var baseName = Path.GetFileNameWithoutExtension(basePath);
        var extension = Path.GetExtension(basePath);

        // 삭제 후 같은 경로에 다시 올렸다가 같은 날 또 지우면 겹침. 덮어쓰면 먼저 지운 원본이 사라짐.
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
