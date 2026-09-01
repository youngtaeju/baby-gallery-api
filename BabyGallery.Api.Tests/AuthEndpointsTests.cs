using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using BabyGallery.Api.Data.Entities;
using BabyGallery.Api.Endpoints;
using Xunit;

namespace BabyGallery.Api.Tests;

public sealed class AuthEndpointsTests : IClassFixture<ApiFactory>
{
    private const string Password = "familypass1";

    private static CancellationToken TestToken => TestContext.Current.CancellationToken;

    private readonly ApiFactory _factory;

    public AuthEndpointsTests(ApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Me_없는_토큰이면_401()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/auth/me", TestToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Login_비밀번호가_틀리면_401()
    {
        var client = await CreateClientWithUserAsync("wrong-password-user");

        var response = await LoginAsync(client, "wrong-password-user", "not-the-password");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Login_없는_계정이면_401()
    {
        var client = _factory.CreateClient();

        var response = await LoginAsync(client, "no-such-user", Password);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Login_성공하면_토큰과_사용자_정보_반환()
    {
        var client = await CreateClientWithUserAsync("login-success", UserRole.Editor);

        var payload = await LoginAndReadAsync(client, "login-success");

        Assert.False(string.IsNullOrWhiteSpace(payload.AccessToken));
        Assert.False(string.IsNullOrWhiteSpace(payload.RefreshToken));
        Assert.Equal(1800, payload.ExpiresIn);
        Assert.Equal("login-success", payload.User.Username);
        Assert.Equal(nameof(UserRole.Editor), payload.User.Role);
    }

    [Fact]
    public async Task Me_유효한_토큰이면_사용자_정보_반환()
    {
        var client = await CreateClientWithUserAsync("me-user", UserRole.Editor);
        var payload = await LoginAndReadAsync(client, "me-user");

        Authorize(client, payload.AccessToken);
        var user = await client.GetFromJsonAsync<UserResponse>("/auth/me", TestToken);

        Assert.NotNull(user);
        Assert.Equal("me-user", user.Username);
        Assert.Equal(nameof(UserRole.Editor), user.Role);
    }

    [Fact]
    public async Task Me_위조된_토큰이면_401()
    {
        var client = await CreateClientWithUserAsync("tampered-user");
        var payload = await LoginAndReadAsync(client, "tampered-user");

        Authorize(client, payload.AccessToken + "tampered");
        var response = await client.GetAsync("/auth/me", TestToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Refresh_새_토큰쌍으로_회전()
    {
        var client = await CreateClientWithUserAsync("rotate-user");
        var first = await LoginAndReadAsync(client, "rotate-user");

        var second = await RefreshAndReadAsync(client, first.RefreshToken);

        Assert.NotEqual(first.RefreshToken, second.RefreshToken);
        Assert.Equal("rotate-user", second.User.Username);
    }

    [Fact]
    public async Task Refresh_폐기된_토큰_재사용시_모든_세션_차단()
    {
        var client = await CreateClientWithUserAsync("reuse-user");
        var first = await LoginAndReadAsync(client, "reuse-user");
        var second = await RefreshAndReadAsync(client, first.RefreshToken);

        // 이미 회전으로 폐기된 토큰 재제시.
        var reused = await RefreshAsync(client, first.RefreshToken);
        Assert.Equal(HttpStatusCode.Unauthorized, reused.StatusCode);

        // 탈취로 간주해 직전에 발급한 정상 토큰까지 폐기.
        var afterDetection = await RefreshAsync(client, second.RefreshToken);
        Assert.Equal(HttpStatusCode.Unauthorized, afterDetection.StatusCode);
    }

    [Fact]
    public async Task Refresh_임의의_문자열이면_401()
    {
        var client = _factory.CreateClient();

        var response = await RefreshAsync(client, "not-a-real-token");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Logout_인증_없이_호출하면_401()
    {
        var client = await CreateClientWithUserAsync("logout-anon");
        var payload = await LoginAndReadAsync(client, "logout-anon");

        var response = await client.PostAsJsonAsync("/auth/logout", new RefreshRequest(payload.RefreshToken), TestToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Logout_이후_해당_refresh_token_거부()
    {
        var client = await CreateClientWithUserAsync("logout-user");
        var payload = await LoginAndReadAsync(client, "logout-user");

        Authorize(client, payload.AccessToken);
        var logout = await client.PostAsJsonAsync("/auth/logout", new RefreshRequest(payload.RefreshToken), TestToken);
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);

        var refresh = await RefreshAsync(client, payload.RefreshToken);
        Assert.Equal(HttpStatusCode.Unauthorized, refresh.StatusCode);
    }

    private async Task<HttpClient> CreateClientWithUserAsync(string username, UserRole role = UserRole.Viewer)
    {
        await _factory.AddUserAsync(username, Password, role);
        return _factory.CreateClient();
    }

    private static void Authorize(HttpClient client, string accessToken)
    {
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
    }

    private static Task<HttpResponseMessage> LoginAsync(HttpClient client, string username, string password)
    {
        return client.PostAsJsonAsync("/auth/login", new LoginRequest(username, password), TestToken);
    }

    private static Task<HttpResponseMessage> RefreshAsync(HttpClient client, string refreshToken)
    {
        return client.PostAsJsonAsync("/auth/refresh", new RefreshRequest(refreshToken), TestToken);
    }

    private static async Task<AuthResponse> LoginAndReadAsync(HttpClient client, string username)
    {
        var response = await LoginAsync(client, username, Password);
        response.EnsureSuccessStatusCode();

        return (await response.Content.ReadFromJsonAsync<AuthResponse>(TestToken))!;
    }

    private static async Task<AuthResponse> RefreshAndReadAsync(HttpClient client, string refreshToken)
    {
        var response = await RefreshAsync(client, refreshToken);
        response.EnsureSuccessStatusCode();

        return (await response.Content.ReadFromJsonAsync<AuthResponse>(TestToken))!;
    }
}
