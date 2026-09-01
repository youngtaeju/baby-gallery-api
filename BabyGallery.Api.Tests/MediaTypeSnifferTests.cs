using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using BabyGallery.Api.Data.Entities;
using BabyGallery.Api.Services;
using Xunit;

namespace BabyGallery.Api.Tests;

// 내용 판별의 저장 확장자와 확장자 기반 인덱싱 타입 간 일관성 검증.
public sealed class MediaTypeSnifferTests
{
    // 실제 파일에서 추출한 헤더. 합성 바이트로 재현하기 어려운 QuickTime 변형 포함.
    // IMG_8932.MOV (iPhone 17) — ftyp 박스 없이 wide → mdat → moov.
    private const string RealMovWithoutFtyp =
        "00000008776964650067b13a6d6461740000004506053b47564adc5c4c433f94";

    // IMG_8859.MOV — ftyp 'qt  '.
    private const string RealMovWithFtyp =
        "00000014667479707174202000000000717420200000000877696465003b2f67";

    // IMG_8868.JPG — JFIF APP0.
    private const string RealJpegJfif =
        "ffd8ffe000144a46494600010101012c012c0000414d5046ffe10a9c45786966";

    // IMG_8976.JPG — EXIF APP1.
    private const string RealJpegExif =
        "ffd8ffe12a3445786966000049492a00080000000c000001040001000000a00f";

    // 카카오톡 저장 영상 — ftyp 'isom', 호환 'isom iso2 mp41'.
    private const string RealMp4 =
        "0000001c6674797069736f6d0000020069736f6d69736f326d70343100000008";

    [Fact]
    public void ftyp가_없는_실물_QuickTime을_영상으로_판별()
    {
        // QuickTime File Format에서 ftyp는 선택 사항. ftyp만 검사하면 실제 파일의 재업로드가 거부됨.
        AssertDetected(FromHex(RealMovWithoutFtyp), MediaType.Video, ".mov");
    }

    [Fact]
    public void ftyp가_있는_실물_QuickTime을_영상으로_판별()
    {
        AssertDetected(FromHex(RealMovWithFtyp), MediaType.Video, ".mov");
    }

    [Fact]
    public void 실물_MP4를_영상으로_판별()
    {
        AssertDetected(FromHex(RealMp4), MediaType.Video, ".mp4");
    }

    [Fact]
    public void 실물_JFIF_JPEG을_이미지로_판별()
    {
        AssertDetected(FromHex(RealJpegJfif), MediaType.Image, ".jpg");
    }

    [Fact]
    public void 실물_EXIF_JPEG을_이미지로_판별()
    {
        AssertDetected(FromHex(RealJpegExif), MediaType.Image, ".jpg");
    }

    [Fact]
    public void PNG를_이미지로_판별()
    {
        AssertDetected([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D],
            MediaType.Image,
            ".png");
    }

    [Theory]
    [InlineData("GIF87a")]
    [InlineData("GIF89a")]
    public void GIF를_이미지로_판별(string signature)
    {
        var header = new List<byte>(Encoding.ASCII.GetBytes(signature));
        header.AddRange(new byte[12]);

        AssertDetected(header.ToArray(), MediaType.Image, ".gif");
    }

    [Fact]
    public void WebP를_이미지로_판별()
    {
        var header = new List<byte>(Encoding.ASCII.GetBytes("RIFF"));
        header.AddRange([0x20, 0x00, 0x00, 0x00]);
        header.AddRange(Encoding.ASCII.GetBytes("WEBPVP8 "));

        AssertDetected(header.ToArray(), MediaType.Image, ".webp");
    }

    [Theory]
    [InlineData("heic")]
    [InlineData("heix")]
    [InlineData("mif1")]
    [InlineData("msf1")]
    public void HEIF_계열_브랜드를_이미지로_판별(string brand)
    {
        AssertDetected(CreateFtyp(brand), MediaType.Image, ".heic");
    }

    [Theory]
    [InlineData("isom")]
    [InlineData("iso2")]
    [InlineData("mp41")]
    [InlineData("mp42")]
    [InlineData("avc1")]
    public void MP4_계열_브랜드를_영상으로_판별(string brand)
    {
        AssertDetected(CreateFtyp(brand), MediaType.Video, ".mp4");
    }

    [Fact]
    public void M4V_브랜드를_영상으로_판별()
    {
        AssertDetected(CreateFtyp("M4V "), MediaType.Video, ".m4v");
    }

    [Fact]
    public void QuickTime_브랜드를_영상으로_판별()
    {
        AssertDetected(CreateFtyp("qt  "), MediaType.Video, ".mov");
    }

    [Fact]
    public void major_브랜드를_모르면_호환_브랜드로_판별()
    {
        // iso5, iso6처럼 알려지지 않은 major 브랜드와 알려진 호환 브랜드를 함께 쓰는 파일 대응.
        AssertDetected(CreateFtyp("iso5", "isom", "mp41"), MediaType.Video, ".mp4");
    }

    [Fact]
    public void major_브랜드가_호환_브랜드보다_우선()
    {
        AssertDetected(CreateFtyp("qt  ", "isom"), MediaType.Video, ".mov");
    }

    [Fact]
    public void ftyp_없이_wide_박스로_시작하면_QuickTime으로_판별()
    {
        AssertDetected(CreateBox("wide", size: 8), MediaType.Video, ".mov");
    }

    [Theory]
    [InlineData("mdat")]
    [InlineData("moov")]
    [InlineData("free")]
    [InlineData("skip")]
    [InlineData("pnot")]
    public void ftyp_없는_미허용_선두_박스는_QuickTime으로_판별하지_않음(string boxType)
    {
        Assert.False(MediaTypeSniffer.TryDetect(CreateBox(boxType, size: 8), out _, out _));
    }

    [Fact]
    public void 알려지지_않은_ftyp_브랜드는_거부()
    {
        Assert.False(MediaTypeSniffer.TryDetect(CreateFtyp("xxxx", "yyyy"), out _, out _));
    }

    [Fact]
    public void wide_박스라도_크기가_비정상이면_거부()
    {
        // 박스 크기가 헤더의 8바이트에도 못 미치면 ISOBMFF 구조로 성립하지 않음.
        Assert.False(MediaTypeSniffer.TryDetect(CreateBox("wide", size: 3), out _, out _));
    }

    [Theory]
    [InlineData(8)]
    [InlineData(15)]
    public void ftyp_박스가_major_브랜드를_담을_수_없으면_거부(int declaredSize)
    {
        var header = CreateFtyp("isom");
        BigEndian(declaredSize).CopyTo(header, 0);

        Assert.False(MediaTypeSniffer.TryDetect(header, out _, out _));
    }

    [Fact]
    public void 크기_1인_64비트_확장_박스는_거부()
    {
        var header = CreateFtyp("isom");
        BigEndian(1).CopyTo(header, 0);

        Assert.False(MediaTypeSniffer.TryDetect(header, out _, out _));
    }

    [Theory]
    [InlineData("255044462d312e370a")]
    [InlineData("504b0304140000000800")]
    [InlineData("68656c6c6f20776f726c64")]
    public void 화이트리스트_밖_형식은_거부(string hex)
    {
        Assert.False(MediaTypeSniffer.TryDetect(FromHex(hex), out _, out _));
    }

    [Fact]
    public void 입력이_비어_있으면_거부()
    {
        Assert.False(MediaTypeSniffer.TryDetect([], out _, out _));
    }

    [Fact]
    public void 입력이_너무_짧으면_거부()
    {
        Assert.False(MediaTypeSniffer.TryDetect([0xFF, 0xD8], out _, out _));
    }

    [Theory]
    [InlineData(".jpg", MediaType.Image)]
    [InlineData(".png", MediaType.Image)]
    [InlineData(".gif", MediaType.Image)]
    [InlineData(".webp", MediaType.Image)]
    [InlineData(".heic", MediaType.Image)]
    [InlineData(".heif", MediaType.Image)]
    [InlineData(".mp4", MediaType.Video)]
    [InlineData(".m4v", MediaType.Video)]
    [InlineData(".mov", MediaType.Video)]
    public void 확장자_조회가_인덱싱_대상_타입을_반환(string extension, MediaType expected)
    {
        Assert.True(MediaTypeSniffer.TryGetTypeByExtension(extension, out var mediaType));
        Assert.Equal(expected, mediaType);
    }

    [Fact]
    public void 확장자_조회는_대소문자를_구분하지_않음()
    {
        // DSM·SMB로 투입되는 파일은 .JPG, .MOV처럼 대문자 확장자가 흔함.
        Assert.True(MediaTypeSniffer.TryGetTypeByExtension(".MOV", out var mediaType));
        Assert.Equal(MediaType.Video, mediaType);
    }

    [Fact]
    public void 화이트리스트_밖_확장자는_조회되지_않음()
    {
        Assert.False(MediaTypeSniffer.TryGetTypeByExtension(".pdf", out _));
    }

    [Fact]
    public void 판별_결과의_확장자가_확장자_조회와_같은_타입을_가리킴()
    {
        // 편입 파일명은 판별 결과에서, 재인덱싱은 확장자 조회에서 타입을 얻음.
        // 두 경로가 어긋나면 방금 편입한 파일을 스캐너가 다르게 분류함.
        byte[][] headers =
        [
            FromHex(RealJpegJfif),
            FromHex(RealMovWithoutFtyp),
            FromHex(RealMovWithFtyp),
            FromHex(RealMp4),
            CreateFtyp("heic"),
            CreateFtyp("M4V ")
        ];

        foreach (var header in headers)
        {
            Assert.True(MediaTypeSniffer.TryDetect(header, out var detectedType, out var extension));
            Assert.True(MediaTypeSniffer.TryGetTypeByExtension(extension, out var byExtension));
            Assert.Equal(detectedType, byExtension);
        }
    }

    private static void AssertDetected(ReadOnlySpan<byte> header, MediaType expectedType, string expectedExtension)
    {
        Assert.True(MediaTypeSniffer.TryDetect(header, out var mediaType, out var extension));
        Assert.Equal(expectedType, mediaType);
        Assert.Equal(expectedExtension, extension);
    }

    // [size][ftyp][major][minor][compatible...]
    private static byte[] CreateFtyp(string majorBrand, params string[] compatibleBrands)
    {
        var size = 16 + (compatibleBrands.Length * 4);
        var box = new List<byte>();

        box.AddRange(BigEndian(size));
        box.AddRange(Encoding.ASCII.GetBytes("ftyp"));
        box.AddRange(Encoding.ASCII.GetBytes(majorBrand));
        box.AddRange(BigEndian(0));

        foreach (var brand in compatibleBrands)
        {
            box.AddRange(Encoding.ASCII.GetBytes(brand));
        }

        return box.ToArray();
    }

    private static byte[] CreateBox(string boxType, int size)
    {
        var box = new List<byte>();

        box.AddRange(BigEndian(size));
        box.AddRange(Encoding.ASCII.GetBytes(boxType));
        box.AddRange(new byte[8]);

        return box.ToArray();
    }

    private static byte[] BigEndian(int value)
    {
        return [(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value];
    }

    private static byte[] FromHex(string hex)
    {
        return Convert.FromHexString(hex);
    }
}
