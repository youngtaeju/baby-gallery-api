using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using BabyGallery.Api.Data.Entities;
using BabyGallery.Api.Endpoints;
using Xunit;

namespace BabyGallery.Api.Tests;

public sealed class MediaDeleteTests
{
    private const string Password = "familypass1";

    private static readonly DateTime CapturedAt = new(2026, 5, 17, 3, 24, 51, DateTimeKind.Utc);

    private static CancellationToken TestToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task 삭제_인증이_없으면_401()
    {
        using var factory = new ApiFactory();

        var response = await factory.CreateClient().DeleteAsync("/media/1", TestToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task 삭제_viewer_권한이면_403()
    {
        using var factory = new ApiFactory();
        var mediaId = await AddIndexedMediaAsync(factory, "IMG_0001.JPG");
        var client = await CreateClientAsync(factory, "delete-viewer", UserRole.Viewer);

        var response = await client.DeleteAsync($"/media/{mediaId}", TestToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task 삭제_없는_id면_404()
    {
        using var factory = new ApiFactory();
        var client = await CreateClientAsync(factory, "delete-missing", UserRole.Editor);

        var response = await client.DeleteAsync("/media/9999", TestToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task 삭제하면_목록과_상세에서_사라짐()
    {
        using var factory = new ApiFactory();
        var mediaId = await AddIndexedMediaAsync(factory, "IMG_0001.JPG");
        var client = await CreateClientAsync(factory, "delete-list", UserRole.Editor);

        var response = await client.DeleteAsync($"/media/{mediaId}", TestToken);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var detail = await client.GetAsync($"/media/{mediaId}", TestToken);

        Assert.Equal(HttpStatusCode.NotFound, detail.StatusCode);
        Assert.Empty(await ListMediaAsync(client));
    }

    [Fact]
    public async Task 삭제하면_원본이_휴지통으로_이동()
    {
        using var factory = new ApiFactory();
        var mediaId = await AddIndexedMediaAsync(factory, "IMG_0001.JPG");
        var client = await CreateClientAsync(factory, "delete-move", UserRole.Editor);

        await client.DeleteAsync($"/media/{mediaId}", TestToken);

        // 실삭제가 아님. 원래 경로 구조를 유지한 채 날짜 폴더 아래로 이동.
        Assert.False(File.Exists(Path.Combine(factory.GalleryPath, "IMG_0001.JPG")));

        var moved = Assert.Single(Directory.GetFiles(factory.TrashPath, "*", SearchOption.AllDirectories));

        Assert.Equal(
            Path.Combine(TodayFolder(), "IMG_0001.JPG"),
            Path.GetRelativePath(factory.TrashPath, moved));
    }

    [Fact]
    public async Task 삭제하면_원래_경로를_담은_감사_로그가_남음()
    {
        using var factory = new ApiFactory();
        var content = MediaFixtures.CreateJpeg(320, 240, CapturedAt, utcOffset: "+00:00");
        var mediaId = await AddIndexedMediaAsync(factory, "2026/IMG_0001.JPG", content);
        var client = await CreateClientAsync(factory, "delete-audit", UserRole.Editor);

        await client.DeleteAsync($"/media/{mediaId}", TestToken);

        var log = Assert.Single(await factory.GetDeletionsAsync());

        Assert.Equal("2026/IMG_0001.JPG", log.OriginalRelativePath);
        Assert.Equal("IMG_0001.JPG", log.OriginalFileName);
        Assert.Equal(Sha256(content), log.ContentHash);
        Assert.Equal(content.Length, log.FileSize);
        Assert.Equal("delete-audit", log.DeletedByUsername);
        Assert.Null(log.PurgedAt);

        Assert.True(File.Exists(Path.Combine(factory.TrashPath, log.TrashRelativePath)));
    }

    [Fact]
    public async Task 삭제하면_썸네일_캐시가_제거됨()
    {
        using var factory = new ApiFactory();
        var content = MediaFixtures.CreateJpeg(320, 240);
        var mediaId = await AddIndexedMediaAsync(factory, "IMG_0001.JPG", content);
        var client = await CreateClientAsync(factory, "delete-thumb", UserRole.Editor);

        // 캐시를 실제로 만들어 둔 뒤 삭제.
        var cachePath = Path.Combine(factory.ThumbnailPath, Sha256(content)[..2], Sha256(content) + ".webp");

        Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
        await File.WriteAllBytesAsync(cachePath, [0x00], TestToken);

        await client.DeleteAsync($"/media/{mediaId}", TestToken);

        Assert.False(File.Exists(cachePath));
    }

    [Fact]
    public async Task 삭제한_내용을_다시_업로드할_수_있음()
    {
        using var factory = new ApiFactory();
        var content = MediaFixtures.CreateJpeg(320, 240, CapturedAt, utcOffset: "+00:00");
        var mediaId = await AddIndexedMediaAsync(factory, "IMG_0001.JPG", content);
        var client = await CreateClientAsync(factory, "delete-reupload", UserRole.Editor);

        await client.DeleteAsync($"/media/{mediaId}", TestToken);

        var lookup = await client.PostAsJsonAsync(
            "/media/uploads/lookup",
            new UploadLookupRequest([Sha256(content)]),
            TestToken);

        var results = (await lookup.Content.ReadFromJsonAsync<UploadLookupResponse>(TestToken))!.Results;

        Assert.Null(Assert.Single(results).MediaId);

        var commit = await TestUploads.UploadAsync(client, content, "IMG_0001.JPG");

        Assert.Equal(HttpStatusCode.OK, commit.StatusCode);

        var payload = (await commit.Content.ReadFromJsonAsync<UploadCommitResponse>(TestToken))!;

        Assert.False(payload.Duplicate);
        Assert.NotEqual(mediaId, payload.MediaId);
    }

    [Fact]
    public async Task 휴지통_항목은_다음_스캔에서_다시_인덱싱되지_않음()
    {
        using var factory = new ApiFactory();
        var mediaId = await AddIndexedMediaAsync(factory, "IMG_0001.JPG");
        var client = await CreateClientAsync(factory, "delete-rescan", UserRole.Editor);

        await client.DeleteAsync($"/media/{mediaId}", TestToken);

        var result = await factory.ScanAsync();

        Assert.Equal(0, result.Added);
        Assert.Empty(await ListMediaAsync(client));
    }

    [Fact]
    public async Task 삭제하면_원본_조회가_404()
    {
        using var factory = new ApiFactory();
        var mediaId = await AddIndexedMediaAsync(factory, "IMG_0001.JPG");
        var client = await CreateClientAsync(factory, "delete-original", UserRole.Editor);

        await client.DeleteAsync($"/media/{mediaId}", TestToken);

        var response = await client.GetAsync($"/media/{mediaId}/original", TestToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task 같은_날_같은_경로를_다시_삭제해도_먼저_삭제한_항목이_남음()
    {
        using var factory = new ApiFactory();
        var first = MediaFixtures.CreateJpeg(320, 240, CapturedAt, utcOffset: "+00:00", filler: 0x11);
        var mediaId = await AddIndexedMediaAsync(factory, "IMG_0001.JPG", first);
        var client = await CreateClientAsync(factory, "delete-collide", UserRole.Editor);

        await client.DeleteAsync($"/media/{mediaId}", TestToken);

        var second = MediaFixtures.CreateJpeg(320, 240, CapturedAt, utcOffset: "+00:00", filler: 0x22);
        var secondId = await AddIndexedMediaAsync(factory, "IMG_0001.JPG", second);

        await client.DeleteAsync($"/media/{secondId}", TestToken);

        var moved = Directory.GetFiles(factory.TrashPath, "*", SearchOption.AllDirectories);

        Assert.Equal(2, moved.Length);
        Assert.Contains(moved, path => File.ReadAllBytes(path).SequenceEqual(first));
        Assert.Contains(moved, path => File.ReadAllBytes(path).SequenceEqual(second));
    }

    // 휴지통 날짜 폴더는 삭제 시각 기준. 기본 표준시(Asia/Seoul)로 표기.
    private static string TodayFolder()
    {
        var timeZone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Seoul");

        return TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, timeZone)
            .ToString("yyyyMMdd", CultureInfo.InvariantCulture);
    }

    // 파일을 실제로 두고 스캔으로 인덱싱. 삭제는 파일과 인덱스를 함께 다루므로 둘 다 필요.
    private static async Task<int> AddIndexedMediaAsync(ApiFactory factory, string relativePath, byte[]? content = null)
    {
        var fullPath = Path.Combine(factory.GalleryPath, relativePath);

        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        MediaFixtures.Write(fullPath, content ?? MediaFixtures.CreateJpeg(320, 240, CapturedAt, utcOffset: "+00:00"));

        await factory.ScanAsync();

        return await factory.FindMediaIdAsync(relativePath);
    }

    private static string Sha256(byte[] content)
    {
        return Convert.ToHexStringLower(SHA256.HashData(content));
    }

    private static async Task<IReadOnlyList<MediaItemResponse>> ListMediaAsync(HttpClient client)
    {
        var page = await client.GetFromJsonAsync<MediaListResponse>("/media", TestToken);

        Assert.NotNull(page);

        return page.Items;
    }

    private static async Task<HttpClient> CreateClientAsync(ApiFactory factory, string username, UserRole role)
    {
        await factory.AddUserAsync(username, Password, role);

        var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/auth/login", new LoginRequest(username, Password), TestToken);

        response.EnsureSuccessStatusCode();

        var payload = (await response.Content.ReadFromJsonAsync<AuthResponse>(TestToken))!;

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", payload.AccessToken);

        return client;
    }
}
