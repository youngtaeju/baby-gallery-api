using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using FamilyGallery.Api.Data.Entities;
using MetadataExtractor;
using MetadataExtractor.Formats.Exif;
using MetadataExtractor.Formats.Gif;
using MetadataExtractor.Formats.Heif;
using MetadataExtractor.Formats.Jpeg;
using MetadataExtractor.Formats.Png;
using MetadataExtractor.Formats.QuickTime;
using MetadataExtractor.Formats.WebP;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Directory = MetadataExtractor.Directory;

namespace FamilyGallery.Api.Services;

// 목록에 필요한 촬영일시·해상도·재생시간만 추출.
public sealed class MediaMetadataReader
{
    // 포맷별로 해상도 정보가 저장된 디렉터리가 다르므로 먼저 확인된 값을 사용
    private static readonly (Type Directory, int WidthTag, int HeightTag)[] ImageDimensionSources =
    [
        (typeof(JpegDirectory), JpegDirectory.TagImageWidth, JpegDirectory.TagImageHeight),
        (typeof(PngDirectory), PngDirectory.TagImageWidth, PngDirectory.TagImageHeight),
        (typeof(WebPDirectory), WebPDirectory.TagImageWidth, WebPDirectory.TagImageHeight),
        (typeof(GifHeaderDirectory), GifHeaderDirectory.TagImageWidth, GifHeaderDirectory.TagImageHeight),
        (typeof(HeicImagePropertiesDirectory), HeicImagePropertiesDirectory.TagImageWidth, HeicImagePropertiesDirectory.TagImageHeight),
        (typeof(ExifSubIfdDirectory), ExifDirectoryBase.TagExifImageWidth, ExifDirectoryBase.TagExifImageHeight)
    ];

    // 촬영일시로 성립하지 않는 값 차단. mvhd 미설정 시의 1904-01-01, 손상된 EXIF 등.
    private static readonly DateTime EarliestPlausibleCapture = new(1990, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly GalleryTimeZone _timeZone;

    private readonly ILogger<MediaMetadataReader> _logger;

    public MediaMetadataReader(GalleryTimeZone timeZone, ILogger<MediaMetadataReader> logger)
    {
        _timeZone = timeZone;
        _logger = logger;
    }

    /// <summary>촬영일시가 없거나 값이 성립하지 않으면 대체값 사용. 인덱싱과 편입이 공유.</summary>
    public static DateTime ResolveCapturedAt(DateTime? capturedAt, DateTime fallback)
    {
        if (capturedAt is null)
        {
            return fallback;
        }

        var value = capturedAt.Value;

        return value >= EarliestPlausibleCapture && value <= DateTime.UtcNow.AddDays(1)
            ? value
            : fallback;
    }

    public MediaMetadata Read(string filePath, Data.Entities.MediaType mediaType)
    {
        IReadOnlyList<Directory> directories;

        try
        {
            directories = ImageMetadataReader.ReadMetadata(filePath);
        }
        catch (Exception ex) when (ex is ImageProcessingException or IOException or NotSupportedException)
        {
            // 메타데이터 부재·손상은 인덱싱 제외 사유가 아님. 파일시스템 값으로 대체.
            _logger.LogDebug(ex, "메타데이터를 읽지 못했습니다: {FilePath}", filePath);

            return MediaMetadata.Empty;
        }

        return mediaType == Data.Entities.MediaType.Video
            ? ReadVideo(directories)
            : ReadImage(directories);
    }

    private MediaMetadata ReadImage(IReadOnlyList<Directory> directories)
    {
        var (width, height) = ReadImageDimensions(directories);

        // Orientation 5~8은 표시 방향이 90도 회전. 그리드 레이아웃이 쓰는 비율에 맞춰 교환.
        if (IsQuarterTurn(directories))
        {
            (width, height) = (height, width);
        }

        return new MediaMetadata(ReadExifCapturedAt(directories), width, height, null);
    }

    private MediaMetadata ReadVideo(IReadOnlyList<Directory> directories)
    {
        var (width, height) = ReadVideoDimensions(directories);

        return new MediaMetadata(ReadQuickTimeCapturedAt(directories), width, height, ReadDurationMs(directories));
    }

    private DateTime? ReadExifCapturedAt(IReadOnlyList<Directory> directories)
    {
        var subIfd = directories.OfType<ExifSubIfdDirectory>().FirstOrDefault();

        if (subIfd is null || !subIfd.TryGetDateTime(ExifDirectoryBase.TagDateTimeOriginal, out var captured))
        {
            return null;
        }

        var local = DateTime.SpecifyKind(captured, DateTimeKind.Unspecified);
        var offset = ParseUtcOffset(subIfd.GetDescription(ExifDirectoryBase.TagTimeZoneOriginal));

        if (offset is not null)
        {
            return new DateTimeOffset(local, offset.Value).UtcDateTime;
        }

        // ConvertTimeToUtc는 DST 전환 구간의 값에 예외를 던짐. 오프셋 직접 조회로 회피.
        return DateTime.SpecifyKind(local - _timeZone.Value.GetUtcOffset(local), DateTimeKind.Utc);
    }

    // mvhd의 생성 시각은 QuickTime 규격상 UTC 기준의 파일 생성 시각이며, 실제 촬영 시각과 다를 수 있음.
    // Apple 기기는 com.apple.quicktime.creationdate에 오프셋이 포함된 촬영일시를 별도로 기록하므로 해당 값을 우선 사용.
    private DateTime? ReadQuickTimeCapturedAt(IReadOnlyList<Directory> directories)
    {
        var metadata = directories.OfType<QuickTimeMetadataHeaderDirectory>().FirstOrDefault();

        // 값이 DateTime으로 해석되지 않은 경우는 mvhd로 넘김. 검증되지 않은 문자열 파싱 경로를 두지 않음.
        if (metadata?.GetObject(QuickTimeMetadataHeaderDirectory.TagCreationDate) is DateTime creationDate)
        {
            return ToUtc(creationDate);
        }

        var header = directories.OfType<QuickTimeMovieHeaderDirectory>().FirstOrDefault();

        return header is not null && header.TryGetDateTime(QuickTimeMovieHeaderDirectory.TagCreated, out var created)
            ? DateTime.SpecifyKind(created, DateTimeKind.Utc)
            : null;
    }

    // 오프셋을 포함한 값은 Local로 파싱.
    private DateTime ToUtc(DateTime value)
    {
        return value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),

            // ConvertTimeToUtc의 DST 전환 구간 예외 방지를 위해 오프셋을 직접 조회.
            _ => DateTime.SpecifyKind(value - _timeZone.Value.GetUtcOffset(value), DateTimeKind.Utc)
        };
    }

    // MetadataExtractor가 timescale을 반영해 TimeSpan으로 정규화.
    private static int? ReadDurationMs(IReadOnlyList<Directory> directories)
    {
        var header = directories.OfType<QuickTimeMovieHeaderDirectory>().FirstOrDefault();

        if (header?.GetObject(QuickTimeMovieHeaderDirectory.TagDuration) is not TimeSpan duration
            || duration <= TimeSpan.Zero)
        {
            return null;
        }

        return (int)Math.Min(duration.TotalMilliseconds, int.MaxValue);
    }

    private static (int? Width, int? Height) ReadVideoDimensions(IReadOnlyList<Directory> directories)
    {
        // 오디오 트랙의 tkhd도 폭·높이 태그를 갖되 값이 0. 실제 영상 트랙만 채택.
        foreach (var track in directories.OfType<QuickTimeTrackHeaderDirectory>())
        {
            if (!track.TryGetInt32(QuickTimeTrackHeaderDirectory.TagWidth, out var width)
                || !track.TryGetInt32(QuickTimeTrackHeaderDirectory.TagHeight, out var height)
                || width <= 0
                || height <= 0)
            {
                continue;
            }

            // 세로로 촬영한 영상은 tkhd에 가로 해상도와 회전각이 따로 실림.
            if (track.TryGetDouble(QuickTimeTrackHeaderDirectory.TagRotation, out var rotation)
                && IsQuarterTurnRotation(rotation))
            {
                (width, height) = (height, width);
            }

            return (width, height);
        }

        return (null, null);
    }

    private static (int? Width, int? Height) ReadImageDimensions(IReadOnlyList<Directory> directories)
    {
        foreach (var (directoryType, widthTag, heightTag) in ImageDimensionSources)
        {
            var directory = directories.FirstOrDefault(d => d.GetType() == directoryType);

            if (directory is not null
                && directory.TryGetInt32(widthTag, out var width)
                && directory.TryGetInt32(heightTag, out var height)
                && width > 0
                && height > 0)
            {
                return (width, height);
            }
        }

        return (null, null);
    }

    private static bool IsQuarterTurn(IReadOnlyList<Directory> directories)
    {
        var ifd0 = directories.OfType<ExifIfd0Directory>().FirstOrDefault();

        return ifd0 is not null
            && ifd0.TryGetInt32(ExifDirectoryBase.TagOrientation, out var orientation)
            && orientation is >= 5 and <= 8;
    }

    // EXIF 오프셋 태그 형식은 "+09:00" / "-05:00".
    private static TimeSpan? ParseUtcOffset(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var text = value.Trim();

        var sign = text[0] switch
        {
            '+' => 1,
            '-' => -1,
            _ => 0
        };

        if (sign == 0)
        {
            return null;
        }

        return TimeSpan.TryParseExact(text[1..], @"hh\:mm", CultureInfo.InvariantCulture, out var offset)
            ? sign * offset
            : null;
    }

    // 음수 회전각까지 처리하도록 180도 주기로 정규화해 90도 회전 여부 판정.
    // double 값의 부동소수 오차를 고려한 허용 범위 비교.
    private static bool IsQuarterTurnRotation(double rotation)
    {
        return Math.Abs(Math.Abs(rotation % 180) - 90) < 1;
    }
}
