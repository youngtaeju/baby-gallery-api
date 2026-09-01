using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace BabyGallery.Api.Endpoints;

public static class HealthEndpoints
{
    private static readonly string Version =
        Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? "unknown";

    public static IEndpointRouteBuilder MapHealthEndpoints(this IEndpointRouteBuilder app)
    {
        // Cloudflare Tunnel / 컨테이너 헬스체크용. 인증 제외.
        app.MapGet("/health", () => Results.Ok(new { status = "ok", version = Version }))
            .AllowAnonymous()
            .WithName("Health");

        return app;
    }
}
