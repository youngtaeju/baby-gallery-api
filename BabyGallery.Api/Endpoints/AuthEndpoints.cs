using System;
using System.Globalization;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using BabyGallery.Api.Data;
using BabyGallery.Api.Data.Entities;
using BabyGallery.Api.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BabyGallery.Api.Endpoints;

public static class AuthEndpoints
{
    public const string RateLimitPolicy = "auth";

    // 계정 부재 시에도 동일한 검증 비용 유지. 앱 시작 시 1회 계산.
    private static readonly string DummyPasswordHash = BCrypt.Net.BCrypt.HashPassword("baby-gallery");

    private const string InvalidCredentialsMessage = "사용자명 또는 비밀번호가 올바르지 않습니다.";

    private const string InvalidRefreshTokenMessage = "refresh token이 유효하지 않습니다.";

    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/auth");

        // 익명 호출이 가능한 두 경로만 rate limit 대상.
        group.MapPost("/login", LoginAsync).AllowAnonymous().RequireRateLimiting(RateLimitPolicy).WithName("Login");
        group.MapPost("/refresh", RefreshAsync).AllowAnonymous().RequireRateLimiting(RateLimitPolicy).WithName("Refresh");
        group.MapPost("/logout", LogoutAsync).WithName("Logout");
        group.MapGet("/me", GetMeAsync).WithName("Me");

        return app;
    }

    private static async Task<IResult> LoginAsync(
        LoginRequest request,
        AppDbContext db,
        TokenService tokens,
        CancellationToken cancellationToken)
    {
        var username = request.Username?.Trim();

        var user = string.IsNullOrEmpty(username)
            ? null
            : await db.Users.SingleOrDefaultAsync(u => u.Username == username, cancellationToken);

        // 계정 존재 여부가 응답 시간으로 드러나지 않도록 부재 시에도 해시 검증 수행.
        var passwordMatches = BCrypt.Net.BCrypt.Verify(
            request.Password ?? string.Empty,
            user?.PasswordHash ?? DummyPasswordHash);

        if (user is null || !passwordMatches)
        {
            return Unauthorized(InvalidCredentialsMessage);
        }

        var refreshToken = IssueRefreshToken(db, tokens, user.Id);
        await db.SaveChangesAsync(cancellationToken);

        return Results.Ok(new AuthResponse(
            tokens.CreateAccessToken(user),
            refreshToken.Token,
            tokens.AccessTokenLifetimeSeconds,
            ToUserResponse(user)));
    }

    private static async Task<IResult> RefreshAsync(
        RefreshRequest request,
        AppDbContext db,
        TokenService tokens,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.RefreshToken))
        {
            return Unauthorized(InvalidRefreshTokenMessage);
        }

        var hash = TokenService.HashRefreshToken(request.RefreshToken);

        var stored = await db.RefreshTokens
            .Include(t => t.User)
            .SingleOrDefaultAsync(t => t.TokenHash == hash, cancellationToken);

        if (stored?.User is null)
        {
            return Unauthorized(InvalidRefreshTokenMessage);
        }

        var now = DateTime.UtcNow;

        if (stored.RevokedAt is not null)
        {
            // 회전 후 폐기된 토큰의 재제시. 탈취로 보고 해당 사용자의 세션 전체 차단.
            foreach (var token in db.RefreshTokens.Where(t => t.UserId == stored.UserId && t.RevokedAt == null))
            {
                token.RevokedAt = now;
            }

            await db.SaveChangesAsync(cancellationToken);

            loggerFactory.CreateLogger(nameof(AuthEndpoints))
                .LogWarning("폐기된 refresh token 재사용 감지. 사용자 {UserId}의 모든 세션을 차단했습니다.", stored.UserId);

            return Unauthorized(InvalidRefreshTokenMessage);
        }

        if (stored.ExpiresAt <= now)
        {
            return Unauthorized(InvalidRefreshTokenMessage);
        }

        stored.RevokedAt = now;

        var refreshToken = IssueRefreshToken(db, tokens, stored.UserId);

        // 만료 토큰 누적 방지.
        db.RefreshTokens.RemoveRange(
            db.RefreshTokens.Where(t => t.UserId == stored.UserId && t.ExpiresAt < now));

        await db.SaveChangesAsync(cancellationToken);

        return Results.Ok(new AuthResponse(
            tokens.CreateAccessToken(stored.User),
            refreshToken.Token,
            tokens.AccessTokenLifetimeSeconds,
            ToUserResponse(stored.User)));
    }

    private static async Task<IResult> LogoutAsync(
        RefreshRequest request,
        ClaimsPrincipal principal,
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        if (!TryGetUserId(principal, out var userId))
        {
            return Unauthorized(InvalidCredentialsMessage);
        }

        if (!string.IsNullOrWhiteSpace(request.RefreshToken))
        {
            var hash = TokenService.HashRefreshToken(request.RefreshToken);

            var stored = await db.RefreshTokens
                .SingleOrDefaultAsync(t => t.TokenHash == hash && t.UserId == userId, cancellationToken);

            if (stored is not null && stored.RevokedAt is null)
            {
                stored.RevokedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(cancellationToken);
            }
        }

        // 토큰 부재·중복 폐기도 성공 처리. 토큰 존재 여부 노출 방지.
        return Results.NoContent();
    }

    private static async Task<IResult> GetMeAsync(
        ClaimsPrincipal principal,
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        if (!TryGetUserId(principal, out var userId))
        {
            return Unauthorized(InvalidCredentialsMessage);
        }

        var user = await db.Users.SingleOrDefaultAsync(u => u.Id == userId, cancellationToken);

        // 토큰 발급 후 계정이 삭제된 경우.
        if (user is null)
        {
            return Unauthorized(InvalidCredentialsMessage);
        }

        return Results.Ok(ToUserResponse(user));
    }

    private static RefreshTokenPair IssueRefreshToken(AppDbContext db, TokenService tokens, int userId)
    {
        var pair = tokens.CreateRefreshToken();

        db.RefreshTokens.Add(new RefreshToken
        {
            UserId = userId,
            TokenHash = pair.Hash,
            ExpiresAt = pair.ExpiresAt,
            CreatedAt = DateTime.UtcNow
        });

        return pair;
    }

    private static bool TryGetUserId(ClaimsPrincipal principal, out int userId)
    {
        return int.TryParse(
            principal.FindFirstValue(TokenService.UserIdClaimType),
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out userId);
    }

    private static UserResponse ToUserResponse(User user)
    {
        return new UserResponse(user.Id, user.Username, user.DisplayName, user.Role.ToString());
    }

    private static IResult Unauthorized(string detail)
    {
        return Results.Problem(detail: detail, statusCode: StatusCodes.Status401Unauthorized);
    }
}

public sealed record LoginRequest(string? Username, string? Password);

public sealed record RefreshRequest(string? RefreshToken);

public sealed record UserResponse(int Id, string Username, string DisplayName, string Role);

public sealed record AuthResponse(string AccessToken, string RefreshToken, int ExpiresIn, UserResponse User);
