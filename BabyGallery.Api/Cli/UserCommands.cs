using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using BabyGallery.Api.Data;
using BabyGallery.Api.Data.Entities;
using Microsoft.Extensions.DependencyInjection;

namespace BabyGallery.Api.Cli;

// 사용자 관리 API 미제공. 계정 생성·권한 변경은 컨테이너 CLI로만 수행.
public static class UserCommands
{
    private const string CommandName = "user";

    private const int MinPasswordLength = 8;

    private const int MaxUsernameLength = 64;

    private const int MaxDisplayNameLength = 64;

    public static bool Matches(string[] args)
    {
        return args.Length > 0 && string.Equals(args[0], CommandName, StringComparison.Ordinal);
    }

    public static int Execute(IServiceProvider services, string[] args)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var subcommand = args.Length > 1 ? args[1] : null;

        return subcommand switch
        {
            "list" => List(db),
            "add" => Add(db, args),
            "set-role" => SetRole(db, args),
            "set-password" => SetPassword(db, args),
            _ => PrintUsage()
        };
    }

    // API로 계정을 조회할 수단이 없어 권한 변경 대상 확인 경로로 필요.
    private static int List(AppDbContext db)
    {
        var users = db.Users
            .OrderBy(u => u.Id)
            .Select(u => new { u.Username, u.DisplayName, u.Role, u.CreatedAt })
            .ToList();

        if (users.Count == 0)
        {
            Console.WriteLine("등록된 사용자가 없습니다.");
            return 0;
        }

        foreach (var user in users)
        {
            Console.WriteLine($"{user.Username,-16} {user.Role,-8} {user.DisplayName,-16} {user.CreatedAt:yyyy-MM-dd}");
        }

        return 0;
    }

    private static int Add(AppDbContext db, string[] args)
    {
        if (args.Length < 3)
        {
            return PrintUsage();
        }

        var username = args[2];

        if (!IsValidUsername(username))
        {
            return Fail($"사용자명이 올바르지 않습니다. 공백 없이 1~{MaxUsernameLength}자.");
        }

        var options = ParseOptions(args, 3);

        if (!options.TryGetValue("display-name", out var displayName) || string.IsNullOrWhiteSpace(displayName))
        {
            return Fail("--display-name 값이 필요합니다.");
        }

        if (displayName.Length > MaxDisplayNameLength)
        {
            return Fail($"표시 이름은 {MaxDisplayNameLength}자 이내여야 합니다.");
        }

        var role = UserRole.Viewer;

        if (options.TryGetValue("role", out var roleValue) && !TryParseRole(roleValue, out role))
        {
            return Fail("--role 값은 viewer 또는 editor여야 합니다.");
        }

        if (db.Users.Any(u => u.Username == username))
        {
            return Fail($"이미 존재하는 사용자입니다: {username}");
        }

        if (!TryReadNewPassword(out var password))
        {
            return 1;
        }

        db.Users.Add(new User
        {
            Username = username,
            DisplayName = displayName,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(password),
            Role = role,
            CreatedAt = DateTime.UtcNow
        });

        db.SaveChanges();

        Console.WriteLine($"사용자 생성 완료: {username} ({role})");
        return 0;
    }

    private static int SetRole(AppDbContext db, string[] args)
    {
        if (args.Length < 4)
        {
            return PrintUsage();
        }

        if (!TryParseRole(args[3], out var role))
        {
            return Fail("권한 값은 viewer 또는 editor여야 합니다.");
        }

        var user = db.Users.SingleOrDefault(u => u.Username == args[2]);

        if (user is null)
        {
            return Fail($"사용자를 찾을 수 없습니다: {args[2]}");
        }

        user.Role = role;
        db.SaveChanges();

        Console.WriteLine($"권한 변경 완료: {user.Username} → {role}");
        return 0;
    }

    private static int SetPassword(AppDbContext db, string[] args)
    {
        if (args.Length < 3)
        {
            return PrintUsage();
        }

        var user = db.Users.SingleOrDefault(u => u.Username == args[2]);

        if (user is null)
        {
            return Fail($"사용자를 찾을 수 없습니다: {args[2]}");
        }

        if (!TryReadNewPassword(out var password))
        {
            return 1;
        }

        user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(password);

        // 비밀번호 변경 시 기존 세션 무효화.
        var now = DateTime.UtcNow;

        foreach (var token in db.RefreshTokens.Where(t => t.UserId == user.Id && t.RevokedAt == null))
        {
            token.RevokedAt = now;
        }

        db.SaveChanges();

        Console.WriteLine($"비밀번호 변경 완료: {user.Username}");
        return 0;
    }

    private static bool TryReadNewPassword(out string password)
    {
        password = ReadPassword("비밀번호: ");

        if (password.Length < MinPasswordLength)
        {
            Fail($"비밀번호는 {MinPasswordLength}자 이상이어야 합니다.");
            return false;
        }

        if (!string.Equals(password, ReadPassword("비밀번호 확인: "), StringComparison.Ordinal))
        {
            Fail("비밀번호가 일치하지 않습니다.");
            return false;
        }

        return true;
    }

    // 인자 전달 시 셸 히스토리·프로세스 목록에 노출. 표준 입력으로만 수신.
    private static string ReadPassword(string prompt)
    {
        Console.Write(prompt);

        // 파이프 입력에서는 ReadKey 사용 불가.
        if (Console.IsInputRedirected)
        {
            var line = Console.ReadLine() ?? string.Empty;
            Console.WriteLine();
            return line;
        }

        var buffer = new StringBuilder();

        while (true)
        {
            var key = Console.ReadKey(intercept: true);

            if (key.Key == ConsoleKey.Enter)
            {
                Console.WriteLine();
                return buffer.ToString();
            }

            if (key.Key == ConsoleKey.Backspace)
            {
                if (buffer.Length > 0)
                {
                    buffer.Length--;
                }

                continue;
            }

            if (!char.IsControl(key.KeyChar))
            {
                buffer.Append(key.KeyChar);
            }
        }
    }

    // --key value 쌍만 수집. 위치 인자는 호출부에서 처리.
    private static Dictionary<string, string> ParseOptions(string[] args, int start)
    {
        var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        for (var i = start; i < args.Length - 1; i++)
        {
            if (args[i].StartsWith("--", StringComparison.Ordinal))
            {
                options[args[i][2..]] = args[i + 1];
                i++;
            }
        }

        return options;
    }

    // Enum.TryParse는 "0", "1" 같은 숫자도 통과시키므로 미사용.
    private static bool TryParseRole(string? value, out UserRole role)
    {
        switch (value?.ToLowerInvariant())
        {
            case "viewer":
                role = UserRole.Viewer;
                return true;
            case "editor":
                role = UserRole.Editor;
                return true;
            default:
                role = UserRole.Viewer;
                return false;
        }
    }

    private static bool IsValidUsername(string username)
    {
        return !string.IsNullOrWhiteSpace(username)
            && username.Length <= MaxUsernameLength
            && username.All(c => !char.IsWhiteSpace(c));
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        return 1;
    }

    private static int PrintUsage()
    {
        Console.Error.WriteLine("""
            사용법:
              user list
              user add <username> --display-name <표시 이름> [--role viewer|editor]
              user set-role <username> <viewer|editor>
              user set-password <username>

            비밀번호는 실행 후 표준 입력으로 받습니다. --role 기본값은 viewer입니다.
            """);

        return 1;
    }
}
