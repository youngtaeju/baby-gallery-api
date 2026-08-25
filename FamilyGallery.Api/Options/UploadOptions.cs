using System.ComponentModel.DataAnnotations;

namespace FamilyGallery.Api.Options;

public sealed class UploadOptions
{
    public const string SectionName = "Upload";

    // 갤러리 마운트 내부 고정. 다른 볼륨이면 편입 시 rename이 EXDEV로 실패.
    // '.' 시작이라 인덱싱 스캔 순회에서 제외됨.
    [Required]
    public string StagingDirectoryName { get; init; } = ".uploads";

    // 가족 단위 촬영물 기준 상한.
    [Range(1024, 8L * 1024 * 1024 * 1024)]
    public long MaxUploadSizeBytes { get; init; } = 2L * 1024 * 1024 * 1024;

    // 미완료 세션 보존 기간. 경과분은 인덱싱 주기에 정리.
    [Range(1, 168)]
    public int SessionExpirationHours { get; init; } = 24;
}
