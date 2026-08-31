using System;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using FamilyGallery.Api.Data;
using FamilyGallery.Api.Data.Entities;
using FamilyGallery.Api.Options;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FamilyGallery.Api.Services;

// 전송이 끝난 스테이징 파일을 검증하고 원본 트리에 편입.
// 기존 원본을 덮어쓰거나 수정하지 않으며, 검증을 통과한 내용만 새 파일로 추가.
// 성공·중복·검증 실패를 상태로 구분해 반환하며, 응답 코드 결정은 호출부 담당.
public sealed class MediaIngestService(
    AppDbContext db,
    MediaMetadataReader metadataReader,
    GalleryTimeZone timeZone,
    IOptions<GalleryOptions> galleryOptions,
    ILogger<MediaIngestService> logger)
{
    // 신규 파일 기록 위치는 이 하위로 고정.
    private const string UploadsDirectoryName = "Uploads";

    // 같은 초에 찍힌 다른 파일과의 파일명 충돌 회피용.
    private const int HashPrefixLength = 8;

    // 촬영 초와 해시 앞자리가 동시에 겹치는 파일이 이만큼 쌓일 수 없음. 무한 탐색 방지용 상한.
    private const int MaxNameAttempts = 100;

    public async Task<MediaIngestResult> IngestAsync(
        string stagingPath,
        string declaredHash,
        long declaredLength,
        string originalFileName,
        CancellationToken cancellationToken)
    {
        var staged = new FileInfo(stagingPath);

        // 전송 계층이 이미 보장하는 조건. 원본 트리에 넣기 전 파일 상태를 직접 확인.
        if (!staged.Exists || staged.Length != declaredLength)
        {
            logger.LogWarning(
                "전송 길이가 선언값과 다릅니다. 선언 {Declared}바이트, 실제 {Actual}바이트.",
                declaredLength,
                staged.Exists ? staged.Length : -1);

            return MediaIngestResult.ContentMismatch;
        }

        var contentHash = await ComputeContentHashAsync(stagingPath, cancellationToken);

        // 전송 손상과 클라이언트 오류를 여기서 차단. 이후 단계는 내용이 확정된 뒤에만 진행.
        if (!string.Equals(contentHash, declaredHash, StringComparison.Ordinal))
        {
            logger.LogWarning("내용 해시가 선언값과 다릅니다: {FileName}", originalFileName);

            return MediaIngestResult.ContentMismatch;
        }

        var header = await ReadHeaderAsync(stagingPath, cancellationToken);

        if (!MediaTypeSniffer.TryDetect(header, out var mediaType, out var extension))
        {
            logger.LogWarning("화이트리스트 밖 형식입니다: {FileName}", originalFileName);

            return MediaIngestResult.UnsupportedType;
        }

        var metadata = metadataReader.Read(stagingPath, mediaType);
        var capturedAt = MediaMetadataReader.ResolveCapturedAt(metadata.CapturedAt, DateTime.UtcNow);
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(galleryOptions.Value.RootPath));

        if (!TryResolveTargetPath(root, capturedAt, contentHash, extension, out var targetPath))
        {
            logger.LogError("편입 대상 파일명을 정하지 못했습니다: {FileName}", originalFileName);

            return MediaIngestResult.Failed;
        }

        try
        {
            // 같은 마운트라 원자적. 부분 기록 상태가 원본 트리에 노출되지 않음.
            File.Move(stagingPath, targetPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogError(ex, "원본 트리로 옮기지 못했습니다: {TargetPath}", targetPath);

            return MediaIngestResult.Failed;
        }

        return await RegisterAsync(
            new FileInfo(targetPath),
            ToRelativePath(root, targetPath),
            originalFileName,
            contentHash,
            mediaType,
            capturedAt,
            metadata,
            cancellationToken);
    }

    private async Task<MediaIngestResult> RegisterAsync(
        FileInfo file,
        string relativePath,
        string originalFileName,
        string contentHash,
        MediaType mediaType,
        DateTime capturedAt,
        MediaMetadata metadata,
        CancellationToken cancellationToken)
    {
        var item = new MediaItem
        {
            RelativePath = relativePath,
            OriginalFileName = originalFileName,
            ContentHash = contentHash,
            MediaType = mediaType,
            FileSize = file.Length,
            CapturedAt = capturedAt,

            // 스캐너의 변경 감지 기준. 실제 mtime과 어긋나면 스캔마다 이 파일을 다시 해싱.
            FileModifiedAt = file.LastWriteTimeUtc,
            IndexedAt = DateTime.UtcNow,
            Width = metadata.Width,
            Height = metadata.Height,
            DurationMs = metadata.DurationMs
        };

        db.MediaItems.Add(item);

        try
        {
            await db.SaveChangesAsync(cancellationToken);

            return MediaIngestResult.Created(item.Id);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            // 재조회 전에 추적 상태를 떼어내야 함. 남겨 두면 다음 SaveChanges가 같은 위반을 반복.
            db.Entry(item).State = EntityState.Detached;

            return await ResolveConflictAsync(file, contentHash, ex, cancellationToken);
        }
    }

    // 유니크 위반의 대부분은 동시 업로드나 스캐너 선점에 의한 중복 히트.
    // ContentHash 위반이 아닌 경우도 여기서 함께 구분.
    private async Task<MediaIngestResult> ResolveConflictAsync(
        FileInfo file,
        string contentHash,
        DbUpdateException exception,
        CancellationToken cancellationToken)
    {
        var existing = await db.MediaItems
            .AsNoTracking()
            .SingleOrDefaultAsync(m => m.ContentHash == contentHash, cancellationToken);

        if (existing is null)
        {
            // 해시 충돌이 아닌 다른 제약. 방금 옮긴 파일이 어느 행에 속하는지 알 수 없으므로 지우지 않음.
            // 원본 트리에 남은 파일은 다음 스캔이 인덱싱.
            logger.LogError(exception, "인덱스 등록에 실패했습니다: {FullName}", file.FullName);

            return MediaIngestResult.Failed;
        }

        if (string.Equals(ToComparablePath(existing.RelativePath), file.FullName, StringComparison.Ordinal))
        {
            // 스캐너가 먼저 등록한 바로 그 파일. 지우면 인덱스가 가리키는 실체가 사라짐.
            return MediaIngestResult.Duplicate(existing.Id);
        }

        TryDeleteRedundant(file);

        return MediaIngestResult.Duplicate(existing.Id);
    }

    // 연·월 디렉터리와 파일명 시각 모두 촬영일시 기준이며 구성된 표준시의 현지시각으로 표기.
    // CapturedAt 저장값은 UTC지만, DSM·SMB 열람 시 앱에 보이는 날짜와 어긋나지 않는 쪽을 택함.
    private bool TryResolveTargetPath(
        string root,
        DateTime capturedAt,
        string contentHash,
        string extension,
        out string targetPath)
    {
        var local = timeZone.ToLocal(capturedAt);

        var directory = Path.Combine(
            root,
            UploadsDirectoryName,
            local.ToString("yyyy", CultureInfo.InvariantCulture),
            local.ToString("MM", CultureInfo.InvariantCulture));

        Directory.CreateDirectory(directory);

        var baseName =
            $"{local.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture)}_{contentHash[..HashPrefixLength]}";

        targetPath = Path.Combine(directory, baseName + extension);

        // 내용이 같은 파일의 동시 편입이면 대상 파일명이 완전히 겹침.
        // 덮어쓰지 않고 접미사를 붙이며, 중복 여부 판정은 인덱스 등록 단계에 위임.
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

    private void TryDeleteRedundant(FileInfo file)
    {
        try
        {
            file.Delete();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 남아도 다음 스캔이 내용 중복으로 건너뜀. 목록에는 영향 없음.
            logger.LogWarning(ex, "중복 편입분을 지우지 못했습니다: {FullName}", file.FullName);
        }
    }

    private string ToComparablePath(string relativePath)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(galleryOptions.Value.RootPath));

        return Path.GetFullPath(Path.Combine(root, relativePath));
    }

    // SQLITE_CONSTRAINT는 모든 제약 위반을 포괄. UNIQUE 위반만 걸러내려면 확장 코드가 필요.
    private static bool IsUniqueViolation(DbUpdateException exception)
    {
        return exception.InnerException is SqliteException
        {
            SqliteExtendedErrorCode: SQLitePCL.raw.SQLITE_CONSTRAINT_UNIQUE
        };
    }

    private static async Task<byte[]> ReadHeaderAsync(string filePath, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 0,
            FileOptions.Asynchronous);

        // 헤더 길이보다 짧은 파일도 정상 입력. 읽은 만큼만 넘기고 거부 여부는 판별기가 정함.
        var buffer = new byte[MediaTypeSniffer.HeaderLength];
        var read = await stream.ReadAtLeastAsync(
            buffer,
            buffer.Length,
            throwOnEndOfStream: false,
            cancellationToken);

        return read == buffer.Length ? buffer : buffer[..read];
    }

    private static async Task<string> ComputeContentHashAsync(string filePath, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 0,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken));
    }

    private static string ToRelativePath(string root, string fullPath)
    {
        return Path.GetRelativePath(root, fullPath).Replace(Path.DirectorySeparatorChar, '/');
    }
}

public enum MediaIngestStatus
{
    Created,

    // 내용이 같은 항목이 이미 인덱스에 있음. 실패가 아님.
    Duplicate,

    // 크기 또는 해시가 선언값과 불일치.
    ContentMismatch,

    // 매직바이트 판별 결과가 화이트리스트 밖.
    UnsupportedType,

    Failed
}

public sealed record MediaIngestResult(MediaIngestStatus Status, int MediaId)
{
    public static readonly MediaIngestResult ContentMismatch = new(MediaIngestStatus.ContentMismatch, 0);

    public static readonly MediaIngestResult UnsupportedType = new(MediaIngestStatus.UnsupportedType, 0);

    public static readonly MediaIngestResult Failed = new(MediaIngestStatus.Failed, 0);

    public static MediaIngestResult Created(int mediaId) => new(MediaIngestStatus.Created, mediaId);

    public static MediaIngestResult Duplicate(int mediaId) => new(MediaIngestStatus.Duplicate, mediaId);
}
