using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using FamilyGallery.Api.Endpoints;
using Xunit;

namespace FamilyGallery.Api.Tests;

// 목록 응답은 인덱스 전체를 대상으로 함. 테스트마다 별도 fixture로 데이터를 격리.
public sealed class MediaEndpointsTests
{
    private const string Username = "media-user";

    private const string Password = "familypass1";

    // MediaEndpoints의 값과 동일. 어긋나면 보정·기본값 검증이 무력화됨.
    private const int DefaultPageSize = 50;

    private const int MaxPageSize = 200;

    private static CancellationToken TestToken => TestContext.Current.CancellationToken;

    // 촬영일시가 같은 항목이 페이지 경계에 걸리도록 구성.
    private static readonly DateTime SharedCapturedAt = new(2026, 3, 15, 4, 5, 6, DateTimeKind.Utc);

    [Fact]
    public async Task 목록_인증이_없으면_401()
    {
        using var factory = new ApiFactory();

        var response = await factory.CreateClient().GetAsync("/media", TestToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task 상세_인증이_없으면_401()
    {
        using var factory = new ApiFactory();

        var response = await factory.CreateClient().GetAsync("/media/1", TestToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task 목록_촬영일시_내림차순으로_반환()
    {
        using var factory = new ApiFactory();

        await factory.AddMediaAsync(
            ("oldest.jpg", new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc)),
            ("newest.jpg", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)),
            ("middle.jpg", new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc)));

        var client = await CreateAuthorizedClientAsync(factory);
        var page = await ReadPageAsync(client, "/media");

        Assert.Equal(["newest.jpg", "middle.jpg", "oldest.jpg"], page.Items.Select(i => i.FileName));
        Assert.Null(page.NextCursor);
    }

    [Fact]
    public async Task 촬영일시가_같아도_커서_페이징에_중복이나_누락이_없음()
    {
        using var factory = new ApiFactory();

        // 전부 동일한 촬영일시. Id 보조 키가 없으면 경계에서 어긋남.
        var ids = await factory.AddMediaAsync(
            Enumerable.Range(0, 5)
                .Select(index => ($"same-{index}.jpg", SharedCapturedAt))
                .ToArray());

        var client = await CreateAuthorizedClientAsync(factory);
        var collected = new List<int>();
        string? cursor = null;
        var pages = 0;

        do
        {
            var url = cursor is null ? "/media?limit=2" : $"/media?limit=2&cursor={Uri.EscapeDataString(cursor)}";
            var page = await ReadPageAsync(client, url);

            collected.AddRange(page.Items.Select(i => i.Id));
            cursor = page.NextCursor;
            pages++;
        }
        while (cursor is not null && pages < 10);

        Assert.Equal(3, pages);
        Assert.Equal(ids.OrderByDescending(id => id), collected);
        Assert.Equal(collected.Count, collected.Distinct().Count());
    }

    [Fact]
    public async Task limit_미지정이면_기본_페이지_크기()
    {
        using var factory = new ApiFactory();

        // 기본값과 상한을 가리려면 최대 페이지 크기를 넘는 데이터가 필요.
        await SeedAsync(factory, MaxPageSize + 1);

        var client = await CreateAuthorizedClientAsync(factory);
        var page = await ReadPageAsync(client, "/media");

        Assert.Equal(DefaultPageSize, page.Items.Count);
        Assert.NotNull(page.NextCursor);
    }

    [Fact]
    public async Task limit은_허용_범위로_보정()
    {
        using var factory = new ApiFactory();

        await SeedAsync(factory, MaxPageSize + 1);

        var client = await CreateAuthorizedClientAsync(factory);

        Assert.Single((await ReadPageAsync(client, "/media?limit=0")).Items);

        var clamped = await ReadPageAsync(client, "/media?limit=9999");

        Assert.Equal(MaxPageSize, clamped.Items.Count);
        Assert.NotNull(clamped.NextCursor);
    }

    [Theory]
    [InlineData("not-valid-base64!!")]
    [InlineData("YWJjZGVm")]
    [InlineData("")]
    public async Task 잘못된_커서면_400(string cursor)
    {
        using var factory = new ApiFactory();

        await factory.AddMediaAsync(("photo.jpg", SharedCapturedAt));

        var client = await CreateAuthorizedClientAsync(factory);
        var response = await client.GetAsync($"/media?cursor={Uri.EscapeDataString(cursor)}", TestToken);

        // 빈 커서는 커서 미지정과 같게 취급.
        var expected = cursor.Length == 0 ? HttpStatusCode.OK : HttpStatusCode.BadRequest;

        Assert.Equal(expected, response.StatusCode);
    }

    [Fact]
    public async Task 상세_없는_id면_404()
    {
        using var factory = new ApiFactory();

        var client = await CreateAuthorizedClientAsync(factory);
        var response = await client.GetAsync("/media/9999", TestToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task 상세_등록된_항목을_반환()
    {
        using var factory = new ApiFactory();

        var ids = await factory.AddMediaAsync(("2026/photo.jpg", SharedCapturedAt));

        var client = await CreateAuthorizedClientAsync(factory);
        var item = await client.GetFromJsonAsync<MediaItemResponse>($"/media/{ids[0]}", TestToken);

        Assert.NotNull(item);
        Assert.Equal(ids[0], item.Id);
        Assert.Equal("photo.jpg", item.FileName);
        Assert.Equal(SharedCapturedAt, item.CapturedAt);
    }

    [Fact]
    public async Task 응답에_NAS_경로와_해시가_노출되지_않음()
    {
        using var factory = new ApiFactory();

        var ids = await factory.AddMediaAsync(("2026/03/secret-folder/photo.jpg", SharedCapturedAt));

        var client = await CreateAuthorizedClientAsync(factory);

        foreach (var url in new[] { "/media", $"/media/{ids[0]}" })
        {
            var json = await client.GetStringAsync(url, TestToken);

            Assert.DoesNotContain("secret-folder", json, StringComparison.Ordinal);
            Assert.DoesNotContain("relativePath", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("contentHash", json, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task 원본_인증이_없으면_401()
    {
        using var factory = new ApiFactory();

        var response = await factory.CreateClient().GetAsync("/media/1/original", TestToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task 원본_없는_id면_404()
    {
        using var factory = new ApiFactory();

        var client = await CreateAuthorizedClientAsync(factory);
        var response = await client.GetAsync("/media/9999/original", TestToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task 원본_전체_바이트와_내용_기반_ETag_반환()
    {
        using var factory = new ApiFactory();

        var client = await CreateAuthorizedClientAsync(factory);
        var content = MediaFixtures.CreateJpeg(800, 600);
        var id = await AddGalleryFileAsync(factory, client, "2026/photo.jpg", content);

        var response = await client.GetAsync($"/media/{id}/original", TestToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(content, await response.Content.ReadAsByteArrayAsync(TestToken));
        Assert.Equal(content.Length, response.Content.Headers.ContentLength);

        // ETag는 원본 내용의 SHA-256. 파일이 바뀌면 스캐너가 갱신하므로 ETag도 따라감.
        Assert.Equal($"\"{Convert.ToHexStringLower(SHA256.HashData(content))}\"", response.Headers.ETag?.ToString());

        Assert.Equal("bytes", Assert.Single(response.Headers.AcceptRanges));
        Assert.True(response.Headers.CacheControl?.Private);
        Assert.Equal(TimeSpan.FromDays(7), response.Headers.CacheControl?.MaxAge);
    }

    [Theory]
    [InlineData("photo.jpg", "image/jpeg")]
    [InlineData("clip.mp4", "video/mp4")]
    [InlineData("clip.mov", "video/quicktime")]
    public async Task 원본_확장자에_맞는_Content_Type_반환(string fileName, string expected)
    {
        using var factory = new ApiFactory();

        var client = await CreateAuthorizedClientAsync(factory);
        var id = await AddGalleryFileAsync(factory, client, fileName, MediaFixtures.CreateOpaqueBytes(fileName));

        var response = await client.GetAsync($"/media/{id}/original", TestToken);

        Assert.Equal(expected, response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task 원본_Range_요청이면_206과_해당_구간만_반환()
    {
        using var factory = new ApiFactory();

        var client = await CreateAuthorizedClientAsync(factory);
        var content = MediaFixtures.CreateJpeg(800, 600);
        var id = await AddGalleryFileAsync(factory, client, "clip.jpg", content);

        using var request = new HttpRequestMessage(HttpMethod.Get, $"/media/{id}/original");
        request.Headers.Range = new RangeHeaderValue(0, 9);

        var response = await client.SendAsync(request, TestToken);

        Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
        Assert.Equal(content[..10], await response.Content.ReadAsByteArrayAsync(TestToken));
        Assert.Equal(content.Length, response.Content.Headers.ContentRange?.Length);
    }

    [Fact]
    public async Task 원본_범위를_벗어난_Range면_416()
    {
        using var factory = new ApiFactory();

        var client = await CreateAuthorizedClientAsync(factory);
        var content = MediaFixtures.CreateJpeg(800, 600);
        var id = await AddGalleryFileAsync(factory, client, "clip.jpg", content);

        using var request = new HttpRequestMessage(HttpMethod.Get, $"/media/{id}/original");
        request.Headers.Range = new RangeHeaderValue(content.Length + 100, null);

        var response = await client.SendAsync(request, TestToken);

        Assert.Equal(HttpStatusCode.RequestedRangeNotSatisfiable, response.StatusCode);
    }

    [Fact]
    public async Task 원본_ETag가_일치하면_304()
    {
        using var factory = new ApiFactory();

        var client = await CreateAuthorizedClientAsync(factory);
        var id = await AddGalleryFileAsync(factory, client, "photo.jpg", MediaFixtures.CreateJpeg(800, 600));

        var first = await client.GetAsync($"/media/{id}/original", TestToken);

        using var request = new HttpRequestMessage(HttpMethod.Get, $"/media/{id}/original");
        request.Headers.IfNoneMatch.Add(first.Headers.ETag!);

        var response = await client.SendAsync(request, TestToken);

        Assert.Equal(HttpStatusCode.NotModified, response.StatusCode);
    }

    [Fact]
    public async Task 원본_파일이_사라졌으면_404()
    {
        using var factory = new ApiFactory();

        var client = await CreateAuthorizedClientAsync(factory);
        var id = await AddGalleryFileAsync(factory, client, "photo.jpg", MediaFixtures.CreateJpeg(800, 600));

        // 인덱스에는 남아 있으나 파일만 사라진 상태. 다음 스캔 전까지 이 상태가 유지됨.
        File.Delete(Path.Combine(factory.GalleryPath, "photo.jpg"));

        var response = await client.GetAsync($"/media/{id}/original", TestToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task 원본_갤러리_루트를_벗어나는_경로면_404()
    {
        using var factory = new ApiFactory();

        // 스캐너는 이런 값을 만들지 않음. 인덱스가 오염돼도 원본 트리 밖을 열지 않는지 확인.
        var outside = Path.GetFullPath(Path.Combine(factory.GalleryPath, "..", "outside.jpg"));

        MediaFixtures.Write(outside, MediaFixtures.CreateJpeg(800, 600));

        // 파일이 실제로 존재해야 경로 검증이 유일한 차단 수단이 됨.
        Assert.True(File.Exists(outside));

        var ids = await factory.AddMediaAsync(("../outside.jpg", SharedCapturedAt));

        var client = await CreateAuthorizedClientAsync(factory);
        var response = await client.GetAsync($"/media/{ids[0]}/original", TestToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task 원본_HEAD_요청이면_본문_없이_헤더만_반환()
    {
        using var factory = new ApiFactory();

        var client = await CreateAuthorizedClientAsync(factory);
        var content = MediaFixtures.CreateJpeg(800, 600);
        var id = await AddGalleryFileAsync(factory, client, "photo.jpg", content);

        using var request = new HttpRequestMessage(HttpMethod.Head, $"/media/{id}/original");

        var response = await client.SendAsync(request, TestToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(content.Length, response.Content.Headers.ContentLength);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync(TestToken));
    }

    // 스트리밍은 파일과 인덱스 행이 함께 있어야 함. 인덱싱을 거쳐 실제 ContentHash를 남김.
    private static async Task<int> AddGalleryFileAsync(ApiFactory factory, HttpClient client, string relativePath, byte[] content)
    {
        MediaFixtures.Write(Path.Combine(factory.GalleryPath, relativePath), content);

        await factory.ScanAsync();

        var page = await ReadPageAsync(client, "/media");

        return page.Items.Single(i => i.FileName == Path.GetFileName(relativePath)).Id;
    }

    private static Task SeedAsync(ApiFactory factory, int count)
    {
        return factory.AddMediaAsync(
            Enumerable.Range(0, count)
                .Select(index => ($"item-{index}.jpg", SharedCapturedAt))
                .ToArray());
    }

    private static async Task<HttpClient> CreateAuthorizedClientAsync(ApiFactory factory)
    {
        await factory.AddUserAsync(Username, Password);

        var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/auth/login", new LoginRequest(Username, Password), TestToken);

        response.EnsureSuccessStatusCode();

        var payload = (await response.Content.ReadFromJsonAsync<AuthResponse>(TestToken))!;

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", payload.AccessToken);

        return client;
    }

    private static async Task<MediaListResponse> ReadPageAsync(HttpClient client, string url)
    {
        var page = await client.GetFromJsonAsync<MediaListResponse>(url, TestToken);

        Assert.NotNull(page);

        return page;
    }
}
