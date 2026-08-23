using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using FamilyGallery.Api.Data.Entities;
using FamilyGallery.Api.Options;
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

// 목록에 필요한 촬영일시·해상도·재생시간만 추출. 그 외 메타데이터는 사용처 없음.
public sealed class MediaMetadataReader
{
    // 포맷별로 해상도를 싣는 디렉터리가 다름. 먼저 적중한 것을 채택.
    private static readonly (Type Directory, int WidthTag, int HeightTag)[] ImageDimensionSources =
    [
        (typeof(JpegDirectory), JpegDirectory.TagImageWidth, JpegDirectory.TagImageHeight),
        (typeof(PngDirectory), PngDirectory.TagImageWidth, PngDirectory.TagImageHeight),
        (typeof(WebPDirectory), WebPDirectory.TagImageWidth, WebPDirectory.TagImageHeight),
        (typeof(GifHeaderDirectory), GifHeaderDirectory.TagImageWidth, GifHeaderDirectory.TagImageHeight),
        (typeof(HeicImagePropertiesDirectory), HeicImagePropertiesDirectory.TagImageWidth, HeicImagePropertiesDirectory.TagImageHeight),
        (typeof(ExifSubIfdDirectory), ExifDirectoryBase.TagExifImageWidth, ExifDirectoryBase.TagExifImageHeight)
    ];

    private readonly TimeZoneInfo _fallbackTimeZone;

    private readonly ILogger<MediaMetadataReader> _logger;

    public MediaMetadataReader(IOptions<IndexingOptions> options, ILogger<MediaMetadataReader> logger)
    {
        _logger = logger;

        var timeZoneId = options.Value.TimeZone;

        try
        {
            _fallbackTimeZone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            // 시각 해석만 어긋날 뿐 조회 기능은 유지 가능. 기동 실패로 서비스를 내리지 않음.
            _logger.LogError(ex,
                "Indexing:TimeZone 값 '{TimeZone}'을 해석하지 못해 UTC로 대체합니다. 오프셋 태그가 없는 촬영일시가 어긋납니다.",
                timeZoneId);

            _fallbackTimeZone = TimeZoneInfo.Utc;
        }
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
        return DateTime.SpecifyKind(local - _fallbackTimeZone.GetUtcOffset(local), DateTimeKind.Utc);
    }

    // QuickTime 규격상 mvhd의 생성 시각은 UTC.
    // 다만 Apple 기기는 기기 로컬시간을 기록하는 편차가 있어 해당 영상의 촬영일시는 기기 오프셋만큼 어긋남.
    private static DateTime? ReadQuickTimeCapturedAt(IReadOnlyList<Directory> directories)
    {
        var header = directories.OfType<QuickTimeMovieHeaderDirectory>().FirstOrDefault();

        return header is not null && header.TryGetDateTime(QuickTimeMovieHeaderDirectory.TagCreated, out var created)
            ? DateTime.SpecifyKind(created, DateTimeKind.Utc)
            : null;
    }

    // MetadataExtractor가 timescale을 반영해 TimeSpan으로 정규화. 수치 조회 API로는 얻을 수 없음.
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
