using System.ComponentModel.DataAnnotations;

namespace FamilyGallery.Api.Options;

public sealed class ThumbnailOptions
{
    public const string SectionName = "Thumbnail";

    // 디스크 캐시 루트. SQLite와 같은 쓰기 영역이며 원본 볼륨과 분리.
    [Required]
    public string CachePath { get; init; } = string.Empty;

    // PATH에 등록되어 있으면 파일명만으로 충분.
    [Required]
    public string FfmpegPath { get; init; } = "ffmpeg";

    // 동시 생성 수 상한. NAS 디스크와 CPU 경합 억제.
    [Range(1, 16)]
    public int MaxConcurrency { get; init; } = 2;

    // 개별 생성 제한 시간. 손상 파일에서 프로세스가 남지 않도록 함.
    [Range(1, 300)]
    public int TimeoutSeconds { get; init; } = 30;
}
