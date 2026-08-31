using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FamilyGallery.Api.Data.Entities;
using FamilyGallery.Api.Endpoints;
using Xunit;

namespace FamilyGallery.Api.Tests;

// 업로드는 쓰기 경로이므로 editor 권한이 전제.
public sealed class UploadEndpointsTests
{
    private const string Password = "familypass1";

    // 형식만 맞춘 64자 소문자 hex. 실제 파일 내용과 무관.
    private const string KnownHash = "ab2eba284d882c18d7a79228b716d2fc3e7bc7f2d52ece73262fa25d7f91f223";

    private const string UnknownHash = "cd11ba284d882c18d7a79228b716d2fc3e7bc7f2d52ece73262fa25d7f91f223";

    private static CancellationToken TestToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task 중복조회_인증이_없으면_401()
    {
        using var factory = new ApiFactory();

        var response = await factory.CreateClient()
            .PostAsJsonAsync("/media/uploads/lookup", new UploadLookupRequest([KnownHash]), TestToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task 중복조회_viewer_권한이면_403()
    {
        using var factory = new ApiFactory();
        var client = await CreateClientAsync(factory, "lookup-viewer", UserRole.Viewer);

        var response = await client
            .PostAsJsonAsync("/media/uploads/lookup", new UploadLookupRequest([KnownHash]), TestToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task 중복조회_인덱스에_있는_해시는_mediaId를_반환()
    {
        using var factory = new ApiFactory();

        var mediaId = await factory.AddMediaWithHashAsync("Uploads/2026/01/existing.jpg", KnownHash);

        var client = await CreateClientAsync(factory, "lookup-hit", UserRole.Editor);
        var results = await LookupAsync(client, KnownHash);

        var result = Assert.Single(results);

        Assert.Equal(KnownHash, result.Hash);
        Assert.Equal(mediaId, result.MediaId);
    }

    [Fact]
    public async Task 중복조회_인덱스에_없는_해시는_mediaId가_null()
    {
        using var factory = new ApiFactory();
        var client = await CreateClientAsync(factory, "lookup-miss", UserRole.Editor);

        var results = await LookupAsync(client, UnknownHash);

        var result = Assert.Single(results);

        Assert.Equal(UnknownHash, result.Hash);
        Assert.Null(result.MediaId);
    }

    [Fact]
    public async Task 중복조회_요청_순서를_그대로_유지()
    {
        using var factory = new ApiFactory();

        await factory.AddMediaWithHashAsync("Uploads/2026/01/existing.jpg", KnownHash);

        var client = await CreateClientAsync(factory, "lookup-order", UserRole.Editor);
        var results = await LookupAsync(client, UnknownHash, KnownHash);

        // 선택 목록 인덱스 기반 결과 매핑 오류 방지를 위한 요청 순서 보존
        Assert.Equal([UnknownHash, KnownHash], results.Select(r => r.Hash));
        Assert.Null(results[0].MediaId);
        Assert.NotNull(results[1].MediaId);
    }

    [Fact]
    public async Task 중복조회_해시_형식이_틀리면_400()
    {
        using var factory = new ApiFactory();
        var client = await CreateClientAsync(factory, "lookup-badhash", UserRole.Editor);

        // 대문자 hex. 저장값이 소문자라 그대로 조회하면 무조건 미적중이 됨.
        var response = await client.PostAsJsonAsync(
            "/media/uploads/lookup",
            new UploadLookupRequest([KnownHash.ToUpperInvariant()]),
            TestToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task 중복조회_해시가_비어_있으면_400()
    {
        using var factory = new ApiFactory();
        var client = await CreateClientAsync(factory, "lookup-empty", UserRole.Editor);

        var response = await client.PostAsJsonAsync(
            "/media/uploads/lookup",
            new UploadLookupRequest([]),
            TestToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task 중복조회_상한을_넘으면_400()
    {
        using var factory = new ApiFactory();
        var client = await CreateClientAsync(factory, "lookup-toomany", UserRole.Editor);

        var hashes = Enumerable.Range(0, 201)
            .Select(index => index.ToString("x2").PadLeft(64, '0'))
            .ToList();

        var response = await client.PostAsJsonAsync(
            "/media/uploads/lookup",
            new UploadLookupRequest(hashes),
            TestToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task 업로드세션_인증이_없으면_401()
    {
        using var factory = new ApiFactory();

        var response = await factory.CreateClient()
            .SendAsync(CreateSessionRequest(1024, KnownHash, "IMG_0001.JPG"), TestToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task 업로드세션_viewer_권한이면_403()
    {
        using var factory = new ApiFactory();
        var client = await CreateClientAsync(factory, "session-viewer", UserRole.Viewer);

        var response = await client.SendAsync(CreateSessionRequest(1024, KnownHash, "IMG_0001.JPG"), TestToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task 업로드세션_editor는_생성되고_Location을_반환()
    {
        using var factory = new ApiFactory();
        var client = await CreateClientAsync(factory, "session-editor", UserRole.Editor);

        var response = await client.SendAsync(CreateSessionRequest(1024, KnownHash, "IMG_0001.JPG"), TestToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.NotNull(response.Headers.Location);
        Assert.StartsWith("/media/uploads/", response.Headers.Location!.OriginalString, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 업로드세션_contentHash_메타데이터가_없으면_400()
    {
        using var factory = new ApiFactory();
        var client = await CreateClientAsync(factory, "session-nohash", UserRole.Editor);

        var response = await client.SendAsync(
            CreateSessionRequest(1024, contentHash: null, fileName: "IMG_0001.JPG"),
            TestToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task 업로드세션_contentHash_형식이_틀리면_400()
    {
        using var factory = new ApiFactory();
        var client = await CreateClientAsync(factory, "session-badhash", UserRole.Editor);

        var response = await client.SendAsync(
            CreateSessionRequest(1024, KnownHash.ToUpperInvariant(), "IMG_0001.JPG"),
            TestToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task 업로드세션_filename_메타데이터가_없으면_400()
    {
        using var factory = new ApiFactory();
        var client = await CreateClientAsync(factory, "session-noname", UserRole.Editor);

        var response = await client.SendAsync(
            CreateSessionRequest(1024, KnownHash, fileName: null),
            TestToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task 업로드세션_크기_상한을_넘으면_거부()
    {
        using var factory = new ApiFactory(timeZone: null, maxUploadSizeBytes: 4096);
        var client = await CreateClientAsync(factory, "session-toobig", UserRole.Editor);

        var response = await client.SendAsync(CreateSessionRequest(8192, KnownHash, "IMG_0001.JPG"), TestToken);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    [Fact]
    public async Task 업로드세션_스테이징은_갤러리_루트의_uploads_아래에_생성()
    {
        using var factory = new ApiFactory();
        var client = await CreateClientAsync(factory, "session-staging", UserRole.Editor);

        var response = await client.SendAsync(CreateSessionRequest(1024, KnownHash, "IMG_0001.JPG"), TestToken);

        response.EnsureSuccessStatusCode();

        // 다른 마운트에 두면 편입 시 rename이 EXDEV로 실패함. 위치 자체가 계약.
        Assert.True(Directory.Exists(factory.StagingPath));
        Assert.NotEmpty(Directory.GetFiles(factory.StagingPath));
    }

    [Fact]
    public async Task 스테이징_디렉터리의_미디어_파일은_인덱싱되지_않음()
    {
        using var factory = new ApiFactory();

        Directory.CreateDirectory(factory.StagingPath);

        // 업로드 중인 스테이징 파일의 인덱싱 방지를 위한 dot 디렉터리 제외
        MediaFixtures.Write(
            Path.Combine(factory.StagingPath, "decoy.jpg"),
            MediaFixtures.CreateJpeg(640, 480));

        var result = await factory.ScanAsync();

        Assert.Equal(0, result.Added);
    }

    private static async Task<IReadOnlyList<UploadLookupResult>> LookupAsync(HttpClient client, params string[] hashes)
    {
        var response = await client.PostAsJsonAsync(
            "/media/uploads/lookup",
            new UploadLookupRequest(hashes),
            TestToken);

        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadFromJsonAsync<UploadLookupResponse>(TestToken);

        Assert.NotNull(payload);

        return payload.Results;
    }

    [Fact]
    public async Task 업로드세션_취소하면_스테이징이_비워짐()
    {
        using var factory = new ApiFactory();
        var client = await CreateClientAsync(factory, "session-cancel", UserRole.Editor);

        var created = await client.SendAsync(CreateSessionRequest(1024, KnownHash, "IMG_0001.JPG"), TestToken);

        created.EnsureSuccessStatusCode();

        var cancel = new HttpRequestMessage(HttpMethod.Delete, created.Headers.Location!.OriginalString);

        cancel.Headers.TryAddWithoutValidation("Tus-Resumable", "1.0.0");

        var response = await client.SendAsync(cancel, TestToken);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Empty(Directory.GetFiles(factory.StagingPath));
    }

    [Fact]
    public async Task 업로드세션_길이_선언을_미루면_거부()
    {
        using var factory = new ApiFactory();
        var client = await CreateClientAsync(factory, "session-defer", UserRole.Editor);

        var request = new HttpRequestMessage(HttpMethod.Post, "/media/uploads");

        request.Headers.TryAddWithoutValidation("Tus-Resumable", "1.0.0");

        // Upload-Length 없이 생성하면 서버는 세션 생성 시 업로드 크기를 검증할 수 없음.
        request.Headers.TryAddWithoutValidation("Upload-Defer-Length", "1");
        request.Headers.TryAddWithoutValidation(
            "Upload-Metadata",
            $"filename {Convert.ToBase64String(Encoding.UTF8.GetBytes("IMG_0001.JPG"))}," +
            $"contentHash {Convert.ToBase64String(Encoding.UTF8.GetBytes(KnownHash))}");

        var response = await client.SendAsync(request, TestToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task 업로드세션_이어붙이기_요청은_거부()
    {
        using var factory = new ApiFactory();
        var client = await CreateClientAsync(factory, "session-concat", UserRole.Editor);

        var request = CreateSessionRequest(1024, KnownHash, "IMG_0001.JPG");

        // 부분 파일 조립은 편입이 전제하는 단일 파일 모델과 맞지 않음.
        request.Headers.TryAddWithoutValidation("Upload-Concat", "partial");

        var response = await client.SendAsync(request, TestToken);

        // 미허용 확장은 tusdotnet이 처리하지 않아 404로 응답.
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(Directory.GetFiles(factory.StagingPath));
    }

    [Fact]
    public async Task 지원_확장은_실제_사용하는_것만_노출()
    {
        using var factory = new ApiFactory();
        var client = await CreateClientAsync(factory, "session-options", UserRole.Editor);

        var request = new HttpRequestMessage(HttpMethod.Options, "/media/uploads");

        request.Headers.TryAddWithoutValidation("Tus-Resumable", "1.0.0");

        var response = await client.SendAsync(request, TestToken);
        var extensions = response.Headers.GetValues("Tus-Extension").Single().Split(',');

        Assert.Equal(
            ["creation", "expiration", "termination"],
            extensions.Select(e => e.Trim()).Order(StringComparer.Ordinal));
    }

    // tus creation 요청. 메타데이터는 "key base64value" 목록.
    private static HttpRequestMessage CreateSessionRequest(long uploadLength, string? contentHash, string? fileName)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/media/uploads");

        request.Headers.TryAddWithoutValidation("Tus-Resumable", "1.0.0");
        request.Headers.TryAddWithoutValidation(
            "Upload-Length",
            uploadLength.ToString(CultureInfo.InvariantCulture));

        var metadata = new List<string>();

        if (fileName is not null)
        {
            metadata.Add($"filename {Convert.ToBase64String(Encoding.UTF8.GetBytes(fileName))}");
        }

        if (contentHash is not null)
        {
            metadata.Add($"contentHash {Convert.ToBase64String(Encoding.UTF8.GetBytes(contentHash))}");
        }

        if (metadata.Count > 0)
        {
            request.Headers.TryAddWithoutValidation("Upload-Metadata", string.Join(",", metadata));
        }

        return request;
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
