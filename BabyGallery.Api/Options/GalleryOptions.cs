using System.ComponentModel.DataAnnotations;

namespace BabyGallery.Api.Options;

public sealed class GalleryOptions
{
    public const string SectionName = "Gallery";

    // NAS shared folder 마운트 지점. 원본 트리와 업로드 스테이징·휴지통의 공통 루트.
    [Required]
    public string RootPath { get; init; } = string.Empty;

    /// <summary>상대 경로를 절대 경로로 정규화. 갤러리 루트 하위가 아니면 false.</summary>
    public bool TryResolveMediaPath(string relativePath, out string fullPath)
    {
        return TryResolveUnder(RootPath, relativePath, out fullPath);
    }

    /// <summary>지정한 루트 하위로 상대 경로를 정규화. 벗어나면 false. 휴지통 정리도 같은 검증을 사용.</summary>
    public static bool TryResolveUnder(string rootPath, string relativePath, out string fullPath)
    {
        var root = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(rootPath));

        fullPath = System.IO.Path.GetFullPath(System.IO.Path.Combine(root, relativePath));

        return fullPath.StartsWith(root + System.IO.Path.DirectorySeparatorChar, System.StringComparison.Ordinal);
    }
}
