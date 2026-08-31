using System;
using FamilyGallery.Api.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FamilyGallery.Api.Services;

// 구성된 표준시를 한 곳에서 해석해 보관.
// 오프셋 태그가 없는 EXIF 촬영일시 해석과 원본·휴지통 경로의 날짜 표기가 같은 값을 사용해야 함.
public sealed class GalleryTimeZone
{
    public GalleryTimeZone(IOptions<IndexingOptions> options, ILogger<GalleryTimeZone> logger)
    {
        var timeZoneId = options.Value.TimeZone;

        try
        {
            Value = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            // 시각 해석과 경로 표기만 어긋날 뿐 조회 기능은 유지 가능. 기동 실패로 서비스를 내리지 않음.
            logger.LogError(ex,
                "Indexing:TimeZone 값 '{TimeZone}'을 해석하지 못해 UTC로 대체합니다. 오프셋 태그가 없는 촬영일시가 어긋납니다.",
                timeZoneId);

            Value = TimeZoneInfo.Utc;
        }
    }

    public TimeZoneInfo Value { get; }

    /// <summary>UTC 값을 구성된 표준시로 변환. 파일시스템에 남기는 날짜 표기에 사용.</summary>
    public DateTime ToLocal(DateTime utc)
    {
        return TimeZoneInfo.ConvertTimeFromUtc(utc, Value);
    }
}
