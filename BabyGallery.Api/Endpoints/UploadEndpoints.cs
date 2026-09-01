using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BabyGallery.Api.Data;
using BabyGallery.Api.Data.Entities;
using BabyGallery.Api.Options;
using BabyGallery.Api.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using tusdotnet;
using tusdotnet.Interfaces;
using tusdotnet.Models;
using tusdotnet.Models.Configuration;
using tusdotnet.Models.Expiration;

namespace BabyGallery.Api.Endpoints;

public static class UploadEndpoints
{
    // 앱의 한 번 선택 분량 상한. 왕복을 늘리지 않으면서 요청 본문을 제한.
    private const int MaxLookupHashes = 200;

    // SHA-256 소문자 hex 고정 길이.
    private const int ContentHashLength = 64;

    private const string InvalidHashMessage = "해시는 64자 소문자 16진수여야 합니다.";

    private const string EmptyHashesMessage = "조회할 해시가 없습니다.";

    // 클라이언트가 tus creation의 Upload-Metadata에 담아 보내는 키.
    private const string FileNameMetadataKey = "filename";

    private const string ContentHashMetadataKey = "contentHash";

    private const string MissingFileNameMessage = "filename 메타데이터가 필요합니다.";

    private const string MissingContentHashMessage = "contentHash 메타데이터가 필요합니다.";

    private const string ContentMismatchMessage = "전송된 내용이 선언한 크기·해시와 다릅니다.";

    private const string UnsupportedTypeMessage = "지원하지 않는 미디어 형식입니다.";

    private const string IngestFailedMessage = "업로드를 원본에 반영하지 못했습니다.";

    private const string SessionNotFoundMessage = "업로드 세션을 찾을 수 없습니다.";

    private const string SessionIncompleteMessage = "전송이 끝나지 않은 세션입니다.";

    // TusDiskStore가 발급하는 파일 id 길이. GuidFileIdProvider 기준 32자 소문자 hex.
    private const int FileIdLength = 32;

    private static readonly string TooManyHashesMessage =
        $"한 번에 조회할 수 있는 해시는 {MaxLookupHashes}개까지입니다.";

    public static IEndpointRouteBuilder MapUploadEndpoints(this IEndpointRouteBuilder app)
    {
        // 쓰기 경로 전체가 editor 전용. 조회 계열과 같은 그룹에 두지 않음.
        var group = app.MapGroup("/media/uploads").RequireAuthorization(nameof(UserRole.Editor));

        group.MapPost("/lookup", LookupAsync).WithName("LookupUploads");

        // tus 완료 PATCH 안에서는 실패를 알릴 수 없음. tusdotnet이 응답을 204로 마무리하며
        // 이벤트에서 설정한 상태코드를 덮어쓰고, 본문을 쓰면 응답 마무리 단계에서 예외 발생.
        // 검증과 편입을 별도 요청으로 분리해 상태코드와 본문을 온전히 통제.
        group.MapPost("/{fileId}/commit", CommitAsync).WithName("CommitUpload");

        // 라우트 패턴에 파일 id를 넣으면 기동 시 예외. tusdotnet이 직접 붙임.
        // 리터럴 세그먼트인 /lookup이 파일 id 파라미터보다 우선 매칭됨.
        app.MapTus("/media/uploads", CreateTusConfigurationAsync)
            .RequireAuthorization(nameof(UserRole.Editor));

        return app;
    }

    private static Task<DefaultTusConfiguration> CreateTusConfigurationAsync(HttpContext context)
    {
        var options = context.RequestServices.GetRequiredService<IOptions<UploadOptions>>().Value;

        return Task.FromResult(new DefaultTusConfiguration
        {
            Store = context.RequestServices.GetRequiredService<ITusStore>(),

            // 실제 사용하는 확장만 허용.
            AllowedExtensions = new TusExtensions(
                TusExtensions.Creation,
                TusExtensions.Termination,
                TusExtensions.Expiration),

            // MaxAllowedUploadSizeInBytes는 int?. 2GB 초과 상한을 담지 못함.
            MaxAllowedUploadSizeInBytesLong = options.MaxUploadSizeBytes,

            // 생성 시점에만 부여. 전송이 길어져도 만료가 밀리지 않음.
            Expiration = new AbsoluteExpiration(TimeSpan.FromHours(options.SessionExpirationHours)),

            Events = new Events
            {
                OnBeforeCreateAsync = ValidateCreateAsync
            }
        });
    }

    // 바이트 수신 전 차단. 편입 시점 해시 재계산의 대조 기준을 여기서 확정.
    private static Task ValidateCreateAsync(BeforeCreateContext context)
    {
        if (string.IsNullOrWhiteSpace(ReadMetadata(context, FileNameMetadataKey)))
        {
            context.FailRequest(HttpStatusCode.BadRequest, MissingFileNameMessage);

            return Task.CompletedTask;
        }

        var contentHash = ReadMetadata(context, ContentHashMetadataKey);

        if (string.IsNullOrEmpty(contentHash))
        {
            context.FailRequest(HttpStatusCode.BadRequest, MissingContentHashMessage);
        }
        else if (!IsContentHash(contentHash))
        {
            context.FailRequest(HttpStatusCode.BadRequest, InvalidHashMessage);
        }

        return Task.CompletedTask;
    }

    // 전송이 끝난 세션을 검증하고 원본 트리에 편입. 결과는 앱이 항목별 상태를 갱신하는 데 사용.
    private static async Task<IResult> CommitAsync(
        string fileId,
        ITusStore store,
        MediaIngestService ingest,
        IOptions<GalleryOptions> galleryOptions,
        IOptions<UploadOptions> uploadOptions,
        CancellationToken cancellationToken)
    {
        // 저장소 경로를 조합하기 전에 형식을 확인. 클라이언트가 준 값이 경로로 흘러들지 않도록 함.
        if (!IsFileId(fileId) || !await store.FileExistAsync(fileId, cancellationToken))
        {
            return Results.Problem(detail: SessionNotFoundMessage, statusCode: StatusCodes.Status404NotFound);
        }

        var uploadLength = await store.GetUploadLengthAsync(fileId, cancellationToken);
        var uploadOffset = await store.GetUploadOffsetAsync(fileId, cancellationToken);

        if (uploadLength is null || uploadOffset != uploadLength)
        {
            return Results.Problem(detail: SessionIncompleteMessage, statusCode: StatusCodes.Status409Conflict);
        }

        var file = await ((ITusReadableStore)store).GetFileAsync(fileId, cancellationToken);
        var metadata = await file.GetMetadataAsync(cancellationToken);

        var result = await ingest.IngestAsync(
            Path.Combine(uploadOptions.Value.ResolveStagingPath(galleryOptions.Value.RootPath), fileId),
            metadata[ContentHashMetadataKey].GetString(Encoding.UTF8),
            uploadLength.Value,
            metadata[FileNameMetadataKey].GetString(Encoding.UTF8),
            cancellationToken);

        // 편입 후 남은 사이드카와 검증 실패로 스테이징에 남은 본문 정리.
        if (store is ITusTerminationStore termination)
        {
            await termination.DeleteFileAsync(fileId, cancellationToken);
        }

        return result.Status switch
        {
            MediaIngestStatus.Created => Results.Ok(new UploadCommitResponse(result.MediaId, false)),
            MediaIngestStatus.Duplicate => Results.Ok(new UploadCommitResponse(result.MediaId, true)),
            MediaIngestStatus.ContentMismatch => Results.Problem(
                detail: ContentMismatchMessage,
                statusCode: StatusCodes.Status422UnprocessableEntity),
            MediaIngestStatus.UnsupportedType => Results.Problem(
                detail: UnsupportedTypeMessage,
                statusCode: StatusCodes.Status415UnsupportedMediaType),
            _ => Results.Problem(detail: IngestFailedMessage, statusCode: StatusCodes.Status500InternalServerError)
        };
    }

    private static bool IsFileId(string value)
    {
        if (value.Length != FileIdLength)
        {
            return false;
        }

        foreach (var character in value)
        {
            if (character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f'))
            {
                return false;
            }
        }

        return true;
    }

    private static string? ReadMetadata(BeforeCreateContext context, string key)
    {
        return context.Metadata.TryGetValue(key, out var value) ? value.GetString(Encoding.UTF8) : null;
    }

    // 전송 전 중복 판정. 적중분은 앱이 tus 세션을 시작하지 않고 중복으로 표시.
    private static async Task<IResult> LookupAsync(
        UploadLookupRequest request,
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        var hashes = request.Hashes;

        if (hashes is null || hashes.Count == 0)
        {
            return Results.Problem(detail: EmptyHashesMessage, statusCode: StatusCodes.Status400BadRequest);
        }

        if (hashes.Count > MaxLookupHashes)
        {
            return Results.Problem(detail: TooManyHashesMessage, statusCode: StatusCodes.Status400BadRequest);
        }

        if (!hashes.All(IsContentHash))
        {
            return Results.Problem(detail: InvalidHashMessage, statusCode: StatusCodes.Status400BadRequest);
        }

        var distinct = hashes.Distinct(StringComparer.Ordinal).ToList();

        var found = await db.MediaItems
            .AsNoTracking()
            .Where(m => distinct.Contains(m.ContentHash))
            .Select(m => new { m.ContentHash, m.Id })
            .ToDictionaryAsync(m => m.ContentHash, m => m.Id, StringComparer.Ordinal, cancellationToken);

        // 앱이 선택 목록의 인덱스로 결과를 대응시킴. 요청 순서와 중복 항목을 그대로 유지.
        var results = hashes
            .Select(hash => new UploadLookupResult(hash, found.TryGetValue(hash, out var id) ? id : null))
            .ToList();

        return Results.Ok(new UploadLookupResponse(results));
    }

    // 저장값이 소문자 hex. 대문자를 그대로 조회하면 조용히 미적중이 되므로 형식 단계에서 차단.
    private static bool IsContentHash(string? value)
    {
        if (value is null || value.Length != ContentHashLength)
        {
            return false;
        }

        foreach (var character in value)
        {
            if (character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f'))
            {
                return false;
            }
        }

        return true;
    }
}

// 전송 전 중복 판정용. 선택 항목 전체를 왕복 1회로 조회.
public sealed record UploadLookupRequest(IReadOnlyList<string> Hashes);

// MediaId가 null이면 신규. 요청 순서를 그대로 유지.
public sealed record UploadLookupResult(string Hash, int? MediaId);

public sealed record UploadLookupResponse(IReadOnlyList<UploadLookupResult> Results);

// Duplicate가 true면 같은 내용이 이미 인덱스에 등록된 것. 실패가 아님.
public sealed record UploadCommitResponse(int MediaId, bool Duplicate);
