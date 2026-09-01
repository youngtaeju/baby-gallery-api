using System;

namespace BabyGallery.Api.Services;

// 추출 실패나 메타데이터 부재는 개별 필드 null로 표현. 인덱싱 실패 사유가 아님.
public sealed record MediaMetadata(DateTime? CapturedAt, int? Width, int? Height, int? DurationMs)
{
    public static readonly MediaMetadata Empty = new(null, null, null, null);
}
