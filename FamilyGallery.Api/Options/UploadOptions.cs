using System.ComponentModel.DataAnnotations;

namespace FamilyGallery.Api.Options;

public sealed class UploadOptions
{
    public const string SectionName = "Upload";

    [Required]
    public string StagingDirectoryName { get; init; } = ".uploads";

    [Required]
    public string TrashDirectoryName { get; init; } = ".trash";

    // 가족 단위 촬영물 기준 상한.
    [Range(1024, 8L * 1024 * 1024 * 1024)]
    public long MaxUploadSizeBytes { get; init; } = 2L * 1024 * 1024 * 1024;

    // 미완료 세션 보존 기간. 경과분은 인덱싱 주기에 정리.
    [Range(1, 168)]
    public int SessionExpirationHours { get; init; } = 24;

    // tus 저장소 구성과 편입 경로가 같은 값을 봐야 함.
    public string ResolveStagingPath(string galleryRootPath)
    {
        return System.IO.Path.Combine(System.IO.Path.GetFullPath(galleryRootPath), StagingDirectoryName);
    }

    public string ResolveTrashPath(string galleryRootPath)
    {
        return System.IO.Path.Combine(System.IO.Path.GetFullPath(galleryRootPath), TrashDirectoryName);
    }
}
