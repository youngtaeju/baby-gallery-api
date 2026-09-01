using System.ComponentModel.DataAnnotations;

namespace BabyGallery.Api.Options;

public sealed class IndexingOptions
{
    public const string SectionName = "Indexing";

    // DSM·SMB 직접 투입분 반영 주기. API 업로드분은 편입 시점에 즉시 등록되므로 이 주기와 무관.
    [Range(1, 1440)]
    public int IntervalMinutes { get; init; } = 10;

    // EXIF DateTimeOriginal에는 오프셋이 없음. 동반 오프셋 태그마저 없을 때 적용할 표준시.
    [Required]
    public string TimeZone { get; init; } = "Asia/Seoul";
}
