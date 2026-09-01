using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Text;
using BabyGallery.Api.Data.Entities;

namespace BabyGallery.Api.Services;

// 미디어 형식 판별과 인덱싱 대상 확장자 조회의 단일 출처.
// 내용 기반 판별 결과에는 저장 확장자를 포함하며 클라이언트 파일명은 판별 근거에서 제외.
// 지원 형식이 제한적이고 Mime-Detective가 ftyp 없는 QuickTime을 판별하지 못해 직접 구현.
public static class MediaTypeSniffer
{
    // ftyp 호환 브랜드를 함께 검사하기 위해 호출자가 읽어 전달할 헤더 길이.
    public const int HeaderLength = 64;

    private const int BoxHeaderLength = 8;

    private const int BrandLength = 4;

    // ftyp의 major/minor 뒤부터 호환 브랜드가 이어짐.
    private const int CompatibleBrandsOffset = 16;

    private static readonly byte[] JpegSignature = [0xFF, 0xD8, 0xFF];

    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private static readonly FrozenDictionary<string, MediaFormat> FormatsByBrand =
        new Dictionary<string, MediaFormat>(StringComparer.Ordinal)
        {
            ["heic"] = new(MediaType.Image, ".heic"),
            ["heix"] = new(MediaType.Image, ".heic"),
            ["hevc"] = new(MediaType.Image, ".heic"),
            ["hevx"] = new(MediaType.Image, ".heic"),
            ["heim"] = new(MediaType.Image, ".heic"),
            ["heis"] = new(MediaType.Image, ".heic"),
            ["hevm"] = new(MediaType.Image, ".heic"),
            ["hevs"] = new(MediaType.Image, ".heic"),
            ["mif1"] = new(MediaType.Image, ".heic"),
            ["msf1"] = new(MediaType.Image, ".heic"),
            ["qt  "] = new(MediaType.Video, ".mov"),
            ["M4V "] = new(MediaType.Video, ".m4v"),
            ["isom"] = new(MediaType.Video, ".mp4"),
            ["iso2"] = new(MediaType.Video, ".mp4"),
            ["mp41"] = new(MediaType.Video, ".mp4"),
            ["mp42"] = new(MediaType.Video, ".mp4"),
            ["avc1"] = new(MediaType.Video, ".mp4"),
            ["dash"] = new(MediaType.Video, ".mp4")
        }.ToFrozenDictionary(StringComparer.Ordinal);

    // ftyp 없는 iPhone QuickTime 촬영본에서 확인된 선두 박스 타입.
    private const string QuickTimeLeadingBoxType = "wide";

    // 인덱싱 대상 판정의 단일 출처. 스캐너의 확장자 필터와 편입 확장자가 어긋나지 않도록 공유.
    private static readonly FrozenDictionary<string, MediaType> TypesByExtension =
        new Dictionary<string, MediaType>(StringComparer.OrdinalIgnoreCase)
        {
            [".jpg"] = MediaType.Image,
            [".jpeg"] = MediaType.Image,
            [".png"] = MediaType.Image,
            [".gif"] = MediaType.Image,
            [".webp"] = MediaType.Image,
            [".heic"] = MediaType.Image,
            [".heif"] = MediaType.Image,
            [".mp4"] = MediaType.Video,
            [".mov"] = MediaType.Video,
            [".m4v"] = MediaType.Video
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    /// <summary>실제 내용에서 미디어 타입과 저장에 사용할 확장자를 판별.</summary>
    public static bool TryDetect(ReadOnlySpan<byte> header, out MediaType mediaType, out string extension)
    {
        mediaType = default;
        extension = string.Empty;

        if (header.Length < 12)
        {
            return false;
        }

        if (header.StartsWith(JpegSignature))
        {
            return Accept(MediaType.Image, ".jpg", out mediaType, out extension);
        }

        if (header.StartsWith(PngSignature))
        {
            return Accept(MediaType.Image, ".png", out mediaType, out extension);
        }

        if (Matches(header, 0, "GIF87a") || Matches(header, 0, "GIF89a"))
        {
            return Accept(MediaType.Image, ".gif", out mediaType, out extension);
        }

        if (Matches(header, 0, "RIFF") && Matches(header, 8, "WEBP"))
        {
            return Accept(MediaType.Image, ".webp", out mediaType, out extension);
        }

        return TryDetectIsoBaseMedia(header, out mediaType, out extension);
    }

    /// <summary>확장자로 인덱싱 대상 타입 조회. 스캐너의 순회 필터가 사용.</summary>
    public static bool TryGetTypeByExtension(string extension, out MediaType mediaType)
    {
        return TypesByExtension.TryGetValue(extension, out mediaType);
    }

    private static bool TryDetectIsoBaseMedia(
        ReadOnlySpan<byte> header,
        out MediaType mediaType,
        out string extension)
    {
        mediaType = default;
        extension = string.Empty;

        // 64비트 확장 크기는 현재 판별 범위에서 제외.
        var boxSize = ReadUInt32(header);

        if (boxSize == 1)
        {
            return false;
        }

        // 크기 0은 파일 끝까지 이어지는 박스.
        if (boxSize is not 0 && boxSize < BoxHeaderLength)
        {
            return false;
        }

        var boxType = ReadAscii(header, BrandLength);

        if (boxType != "ftyp")
        {
            return boxType == QuickTimeLeadingBoxType
                && Accept(MediaType.Video, ".mov", out mediaType, out extension);
        }

        if (boxSize is not 0 && boxSize < CompatibleBrandsOffset)
        {
            return false;
        }

        // major 브랜드가 우선. 알 수 없으면 호환 브랜드 목록을 순서대로 확인.
        if (FormatsByBrand.TryGetValue(ReadAscii(header, BoxHeaderLength), out var format))
        {
            return Accept(format.MediaType, format.Extension, out mediaType, out extension);
        }

        // 호환 브랜드 조회를 ftyp 박스 안으로 제한. 크기 0이면 읽은 범위만 사용.
        var available = boxSize == 0 ? header.Length : (int)Math.Min(boxSize, (uint)header.Length);

        for (var offset = CompatibleBrandsOffset; offset + BrandLength <= available; offset += BrandLength)
        {
            if (FormatsByBrand.TryGetValue(ReadAscii(header, offset), out var compatible))
            {
                return Accept(compatible.MediaType, compatible.Extension, out mediaType, out extension);
            }
        }

        return false;
    }

    private static bool Accept(
        MediaType detectedType,
        string detectedExtension,
        out MediaType mediaType,
        out string extension)
    {
        mediaType = detectedType;
        extension = detectedExtension;

        return true;
    }

    private static bool Matches(ReadOnlySpan<byte> header, int offset, string ascii)
    {
        return offset + ascii.Length <= header.Length
            && ReadAscii(header, offset, ascii.Length) == ascii;
    }

    private static string ReadAscii(ReadOnlySpan<byte> header, int offset, int length = BrandLength)
    {
        return offset + length > header.Length
            ? string.Empty
            : Encoding.ASCII.GetString(header.Slice(offset, length));
    }

    private static uint ReadUInt32(ReadOnlySpan<byte> header)
    {
        return ((uint)header[0] << 24) | ((uint)header[1] << 16) | ((uint)header[2] << 8) | header[3];
    }

    private readonly record struct MediaFormat(MediaType MediaType, string Extension);
}
