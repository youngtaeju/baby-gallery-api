using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using BabyGallery.Api.Endpoints;
using Xunit;

namespace BabyGallery.Api.Tests;

// 인증 테스트와 rate limit 예산을 공유하지 않도록 별도 fixture 사용.
public sealed class RateLimitTests : IClassFixture<ApiFactory>
{
    private const string Username = "ratelimit-user";

    private const string Password = "familypass1";

    private static CancellationToken TestToken => TestContext.Current.CancellationToken;

    private readonly ApiFactory _factory;

    public RateLimitTests(ApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Login_임계치_초과시_429와_RetryAfter_반환()
    {
        await _factory.AddUserAsync(Username, Password);
        var client = _factory.CreateClient();

        HttpResponseMessage? rejected = null;

        for (var attempt = 0; attempt < 40 && rejected is null; attempt++)
        {
            var response = await client.PostAsJsonAsync("/auth/login", new LoginRequest(Username, "wrong"), TestToken);

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                rejected = response;
                break;
            }

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        Assert.NotNull(rejected);
        Assert.NotNull(rejected.Headers.RetryAfter);
        Assert.Equal("application/problem+json", rejected.Content.Headers.ContentType?.MediaType);

        // 차단 중에는 올바른 자격 증명도 거부.
        var blocked = await client.PostAsJsonAsync("/auth/login", new LoginRequest(Username, Password), TestToken);
        Assert.Equal(HttpStatusCode.TooManyRequests, blocked.StatusCode);
    }

    [Fact]
    public async Task Health_는_제한_대상이_아님()
    {
        var client = _factory.CreateClient();

        for (var attempt = 0; attempt < 30; attempt++)
        {
            var response = await client.GetAsync("/health", TestToken);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
    }
}
