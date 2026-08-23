using System;
using System.Buffers.Text;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FamilyGallery.Api.Data;
using FamilyGallery.Api.Data.Entities;
using FamilyGallery.Api.Options;
using FamilyGallery.Api.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

namespace FamilyGallery.Api.Endpoints;

public static class MediaEndpoints
{
    private const int DefaultPageSize = 50;

    private const int MaxPageSize = 200;

    private const string InvalidCursorMessage = "cursor 값이 올바르지 않습니다.";

    private const string MediaNotFoundMessage = "미디어를 찾을 수 없습니다.";

    private const string ThumbnailUnavailableMessage = "썸네일을 생성할 수 없습니다.";

    private const string ThumbnailContentType = "image/webp";

    // API 경유 원본 변경 경로 부재로 장기 캐시 적용.
    // DSM·SMB 직접 교체 시 max-age 동안 이전 응답 유지 가능.
    private const string CacheControl = "private, max-age=604800";

    private static readonly FrozenDictionary<string, string> ContentTypesByExtension =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [".jpg"] = "image/jpeg",
            [".jpeg"] = "image/jpeg",
            [".png"] = "image/png",
            [".gif"] = "image/gif",
            [".webp"] = "image/webp",
            [".heic"] = "image/heic",
            [".heif"] = "image/heif",
            [".mp4"] = "video/mp4",
            [".mov"] = "video/quicktime",
            [".m4v"] = "video/x-m4v"
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    public static IEndpointRouteBuilder MapMediaEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/media");

        // 조회 범위는 사용자별로 구분하지 않음. 인증만 통과하면 전체 미디어 접근 가능.
        group.MapGet("/", ListAsync).WithName("ListMedia");
        group.MapGet("/{id:int}", GetAsync).WithName("GetMedia");

        // HEAD는 영상 플레이어가 재생 전에 길이와 Range 지원 여부를 조회하는 경우 대비.
        group.MapMethods("/{id:int}/original", ["GET", "HEAD"], GetOriginalAsync).WithName("GetMediaOriginal");
        group.MapGet("/{id:int}/thumbnail", GetThumbnailAsync).WithName("GetMediaThumbnail");

        return app;
    }

    private static async Task<IResult> ListAsync(
        AppDbContext db,
        CancellationToken cancellationToken,
        string? cursor = null,
        int? limit = null)
    {
        var pageSize = Math.Clamp(limit ?? DefaultPageSize, 1, MaxPageSize);
        var query = db.MediaItems.AsNoTracking();

        if (!string.IsNullOrEmpty(cursor))
        {
            if (!TryParseCursor(cursor, out var capturedAt, out var id))
            {
                return Results.Problem(detail: InvalidCursorMessage, statusCode: StatusCodes.Status400BadRequest);
            }

            // 촬영일시가 같은 항목이 페이지 경계에서 누락되거나 중복되지 않도록 Id를 보조 키로 사용.
            query = query.Where(m => m.CapturedAt < capturedAt || (m.CapturedAt == capturedAt && m.Id < id));
        }

        // 다음 페이지 존재 여부 판단용으로 1건 더 조회.
        var items = await query
            .OrderByDescending(m => m.CapturedAt)
            .ThenByDescending(m => m.Id)
            .Take(pageSize + 1)
            .ToListAsync(cancellationToken);

        var hasMore = items.Count > pageSize;

        if (hasMore)
        {
            items.RemoveAt(pageSize);
        }

        return Results.Ok(new MediaListResponse(
            items.Select(ToResponse).ToList(),
            hasMore ? CreateCursor(items[^1]) : null));
    }

    private static async Task<IResult> GetAsync(int id, AppDbContext db, CancellationToken cancellationToken)
    {
        var item = await db.MediaItems.AsNoTracking().SingleOrDefaultAsync(m => m.Id == id, cancellationToken);

        return item is null
            ? Results.Problem(detail: MediaNotFoundMessage, statusCode: StatusCodes.Status404NotFound)
            : Results.Ok(ToResponse(item));
    }

    private static async Task<IResult> GetOriginalAsync(
        int id,
        HttpContext context,
        AppDbContext db,
        IOptions<GalleryOptions> galleryOptions,
        CancellationToken cancellationToken)
    {
        var item = await db.MediaItems.AsNoTracking().SingleOrDefaultAsync(m => m.Id == id, cancellationToken);

        // 스캔 이후 삭제된 파일은 다음 스캔까지 인덱스에 남음. 인덱스 유무만으로 판단하지 않음.
        if (item is null
            || !TryResolveGalleryPath(galleryOptions.Value.RootPath, item.RelativePath, out var fullPath)
            || !File.Exists(fullPath))
        {
            return Results.Problem(detail: MediaNotFoundMessage, statusCode: StatusCodes.Status404NotFound);
        }

        context.Response.Headers.CacheControl = CacheControl;

        // Range·If-Range·If-None-Match 해석과 206·416 응답은 Results.File이 처리.
        return Results.File(
            fullPath,
            ResolveContentType(item.RelativePath),
            entityTag: new EntityTagHeaderValue($"\"{item.ContentHash}\""),
            enableRangeProcessing: true);
    }

    private static async Task<IResult> GetThumbnailAsync(
        int id,
        HttpContext context,
        AppDbContext db,
        IOptions<GalleryOptions> galleryOptions,
        ThumbnailService thumbnails,
        CancellationToken cancellationToken)
    {
        var item = await db.MediaItems.AsNoTracking().SingleOrDefaultAsync(m => m.Id == id, cancellationToken);

        if (item is null
            || !TryResolveGalleryPath(galleryOptions.Value.RootPath, item.RelativePath, out var sourcePath)
            || !File.Exists(sourcePath))
        {
            return Results.Problem(detail: MediaNotFoundMessage, statusCode: StatusCodes.Status404NotFound);
        }

        var thumbnailPath = await thumbnails.GetOrCreateAsync(item, sourcePath, cancellationToken);

        // 손상 파일이나 디코딩 불가 포맷. 실패를 캐시에 남기지 않아 다음 요청에 다시 시도됨.
        if (thumbnailPath is null)
        {
            return Results.Problem(detail: ThumbnailUnavailableMessage, statusCode: StatusCodes.Status404NotFound);
        }

        context.Response.Headers.CacheControl = CacheControl;

        // 규격 토큰을 포함해 크기·품질이 바뀌면 클라이언트 캐시가 함께 무효화됨.
        return Results.File(
            thumbnailPath,
            ThumbnailContentType,
            entityTag: new EntityTagHeaderValue($"\"{item.ContentHash}-{ThumbnailService.SpecToken}\""));
    }

    // RelativePath는 스캐너가 기록한 값이지만 인덱스 오염에 대비해 원본 트리 하위인지 확인.
    private static bool TryResolveGalleryPath(string rootPath, string relativePath, out string fullPath)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));

        fullPath = Path.GetFullPath(Path.Combine(root, relativePath));

        return fullPath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    // 화이트리스트 밖 확장자는 인덱싱되지 않으나, 응답 헤더를 비워 두지 않도록 기본값 지정.
    private static string ResolveContentType(string relativePath)
    {
        return ContentTypesByExtension.TryGetValue(Path.GetExtension(relativePath), out var contentType)
            ? contentType
            : "application/octet-stream";
    }

    // 클라이언트가 내부 구조에 의존하지 않도록 불투명 문자열로 전달.
    private static string CreateCursor(MediaItem item)
    {
        return Base64Url.EncodeToString(
            Encoding.UTF8.GetBytes($"{item.CapturedAt.Ticks.ToString(CultureInfo.InvariantCulture)}_{item.Id.ToString(CultureInfo.InvariantCulture)}"));
    }

    // 밀리초 등으로 절삭하면 같은 초 안의 항목이 경계에서 어긋남. tick 그대로 왕복.
    private static bool TryParseCursor(string cursor, out DateTime capturedAt, out int id)
    {
        capturedAt = default;
        id = 0;

        byte[] decoded;

        try
        {
            decoded = Base64Url.DecodeFromChars(cursor);
        }
        catch (FormatException)
        {
            return false;
        }

        var parts = Encoding.UTF8.GetString(decoded).Split('_');

        if (parts.Length != 2
            || !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out id)
            || ticks > DateTime.MaxValue.Ticks)
        {
            return false;
        }

        capturedAt = new DateTime(ticks, DateTimeKind.Utc);

        return true;
    }

    // NAS 실제 경로는 응답에 싣지 않음. 미디어 참조는 Id 기반.
    private static MediaItemResponse ToResponse(MediaItem item)
    {
        return new MediaItemResponse(
            item.Id,
            item.MediaType.ToString(),
            item.OriginalFileName,
            item.FileSize,
            item.CapturedAt,
            item.Width,
            item.Height,
            item.DurationMs);
    }
}

public sealed record MediaItemResponse(
    int Id,
    string MediaType,
    string FileName,
    long FileSize,
    DateTime CapturedAt,
    int? Width,
    int? Height,
    int? DurationMs);

// nextCursor가 null이면 마지막 페이지.
public sealed record MediaListResponse(IReadOnlyList<MediaItemResponse> Items, string? NextCursor);
