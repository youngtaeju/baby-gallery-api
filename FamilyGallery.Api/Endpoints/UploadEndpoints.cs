using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FamilyGallery.Api.Data;
using FamilyGallery.Api.Data.Entities;
using FamilyGallery.Api.Options;
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

namespace FamilyGallery.Api.Endpoints;

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

    private static readonly string TooManyHashesMessage =
        $"한 번에 조회할 수 있는 해시는 {MaxLookupHashes}개까지입니다.";

    public static IEndpointRouteBuilder MapUploadEndpoints(this IEndpointRouteBuilder app)
    {
        // 쓰기 경로 전체가 editor 전용. 조회 계열과 같은 그룹에 두지 않음.
        var group = app.MapGroup("/media/uploads").RequireAuthorization(nameof(UserRole.Editor));

        group.MapPost("/lookup", LookupAsync).WithName("LookupUploads");

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
