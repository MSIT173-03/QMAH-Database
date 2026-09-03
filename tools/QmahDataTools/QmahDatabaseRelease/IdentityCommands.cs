using System.Security.Cryptography;
using System.Text;

using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using QMAH.Infrastructure.Data;
using QMAH.Infrastructure.Models.Entities;
using QMAH.Infrastructure.Models.Identity;

namespace QMAH.DataTools;

public static class IdentityCommands
{
    private const string DefaultConnection =
        "Server=(localdb)\\MSSQLLocalDB;Database=QMAH;Trusted_Connection=True;TrustServerCertificate=True;MultipleActiveResultSets=False";

    public static async Task ResetPasswordAsync(
        string connection,
        string email,
        string? requestedPassword,
        string? credentialsPath,
        string? backupPath)
    {
        var normalizedEmail = email.Trim();
        if (!IsValidEmail(normalizedEmail))
        {
            throw new ArgumentException("--email 必須是有效的 Email。", nameof(email));
        }

        var password = string.IsNullOrWhiteSpace(requestedPassword)
            ? RequireStoredPassword(LoadShowcasePasswords(credentialsPath, backupPath), normalizedEmail)
            : requestedPassword!;

        await using var provider = BuildIdentityProvider(connection);
        using var scope = provider.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var db = scope.ServiceProvider.GetRequiredService<QmahDbContext>();
        var user = await userManager.FindByEmailAsync(normalizedEmail)
            ?? throw new InvalidOperationException($"找不到會員：{normalizedEmail}");

        var token = await userManager.GeneratePasswordResetTokenAsync(user);
        var result = await userManager.ResetPasswordAsync(user, token, password);
        if (!result.Succeeded)
        {
            throw new InvalidOperationException(
                $"會員密碼重設失敗：{string.Join(", ", result.Errors.Select(error => error.Description))}");
        }

        user.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();

        var credential = await BuildCredentialAsync(userManager, db, user, password);
        UpdateCredentialFiles(credential, credentialsPath, backupPath);

        Console.WriteLine($"PASSWORD_RESET|email:{normalizedEmail}");
        Console.WriteLine($"CREDENTIALS|{ResolveCredentialsPath(credentialsPath)}");
        Console.WriteLine($"CREDENTIALS_BACKUP|{ResolveBackupPath(backupPath)}");
    }

    public static async Task SeedShowcaseUsersAsync(
        string connection,
        string? credentialsPath,
        string? backupPath)
    {
        var savedPasswords = LoadShowcasePasswords(credentialsPath, backupPath);
        EnsureShowcasePasswords(savedPasswords);

        // 展示資料以 Email 識別，存在就更新，不存在才新增，重跑不會複製會員
        await using var provider = BuildIdentityProvider(connection);
        using var scope = provider.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
        var db = scope.ServiceProvider.GetRequiredService<QmahDbContext>();
        await EnsureRoleAsync(roleManager, "Admin");
        await EnsureRoleAsync(roleManager, "User");

        var credentials = new List<DemoCredential>(ShowcaseUsers.Count);
        var seededUsers = new List<ShowcaseSeededUser>(ShowcaseUsers.Count);
        var added = 0;
        var updated = 0;
        var baseDate = DateTime.UtcNow.Date;
        await using var transaction = await db.Database.BeginTransactionAsync();

        foreach (var (seed, index) in ShowcaseUsers.Select((seed, index) => (seed, index)))
        {
            // 展示密碼一律從外部憑證檔讀取
            var password = savedPasswords[seed.Email];
            var user = await userManager.FindByEmailAsync(seed.Email);
            if (user is null)
            {
                var createdAt = baseDate.AddDays(-seed.DaysAgo);
                user = new ApplicationUser
                {
                    Id = Guid.NewGuid(),
                    UserName = seed.Email,
                    Email = seed.Email,
                    EmailConfirmed = true,
                    Status = "ACTIVE",
                    CreatedAt = createdAt,
                    UpdatedAt = createdAt
                };
                var createResult = await userManager.CreateAsync(user, password);
                EnsureSucceeded(createResult, $"建立會員 {seed.Email}");
                added++;

                db.UserProfiles.Add(new UserProfile
                {
                    UserId = user.Id,
                    Nickname = seed.DisplayName,
                    AvatarPath = ShowcaseAvatarPaths[index],
                    Visibility = "PUBLIC",
                    Bio = seed.Bio,
                    CreatedAt = createdAt,
                    UpdatedAt = createdAt
                });
            }
            else
            {
                user.Status = "ACTIVE";
                user.EmailConfirmed = true;
                user.UpdatedAt = DateTime.UtcNow;
                var resetToken = await userManager.GeneratePasswordResetTokenAsync(user);
                var resetResult = await userManager.ResetPasswordAsync(user, resetToken, password);
                EnsureSucceeded(resetResult, $"更新會員 {seed.Email} 密碼");

                var profile = await db.UserProfiles
                    .SingleOrDefaultAsync(item => item.UserId == user.Id);
                if (profile is null)
                {
                    db.UserProfiles.Add(new UserProfile
                    {
                        UserId = user.Id,
                        Nickname = seed.DisplayName,
                        AvatarPath = ShowcaseAvatarPaths[index],
                        Visibility = "PUBLIC",
                        Bio = seed.Bio,
                        CreatedAt = user.CreatedAt,
                        UpdatedAt = DateTime.UtcNow
                    });
                }
                else
                {
                    profile.Nickname = seed.DisplayName;
                    profile.AvatarPath = ShowcaseAvatarPaths[index];
                    profile.Visibility = "PUBLIC";
                    profile.Bio = seed.Bio;
                    profile.UpdatedAt = DateTime.UtcNow;
                }

                updated++;
            }

            if (!await userManager.IsInRoleAsync(user, seed.Role))
            {
                var roleResult = await userManager.AddToRoleAsync(user, seed.Role);
                EnsureSucceeded(roleResult, $"設定會員 {seed.Email} 角色");
            }

            credentials.Add(new DemoCredential(
                seed.DisplayName,
                user.Email ?? seed.Email,
                password,
                seed.Role));
            seededUsers.Add(new ShowcaseSeededUser(user, seed, index));
        }

        await EnsureShowcaseAddressesAsync(db, seededUsers);
        await EnsureShowcaseAchievementsAsync(db, seededUsers);
        await db.SaveChangesAsync();
        await transaction.CommitAsync();

        WriteCredentialFiles(credentials, credentialsPath, backupPath);
        Console.WriteLine($"SEEDED_USERS|added:{added}|updated:{updated}|total:{credentials.Count}");
        Console.WriteLine($"CREDENTIALS|{ResolveCredentialsPath(credentialsPath)}");
        Console.WriteLine($"CREDENTIALS_BACKUP|{ResolveBackupPath(backupPath)}");
    }

    private static ServiceProvider BuildIdentityProvider(string connection)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDataProtection();
        services.AddDbContext<QmahDbContext>(options => options.UseSqlServer(
            string.IsNullOrWhiteSpace(connection) ? DefaultConnection : connection));
        services
            .AddIdentityCore<ApplicationUser>(options =>
            {
                options.User.RequireUniqueEmail = true;
                options.Stores.MaxLengthForKeys = 128;
            })
            .AddRoles<IdentityRole<Guid>>()
            .AddEntityFrameworkStores<QmahDbContext>()
            .AddDefaultTokenProviders();
        return services.BuildServiceProvider(validateScopes: true);
    }

    private static async Task EnsureRoleAsync(
        RoleManager<IdentityRole<Guid>> roleManager,
        string role)
    {
        if (await roleManager.RoleExistsAsync(role))
            return;

        var result = await roleManager.CreateAsync(new IdentityRole<Guid>(role));
        EnsureSucceeded(result, $"建立角色 {role}");
    }

    private static void EnsureSucceeded(IdentityResult result, string action)
    {
        if (result.Succeeded)
            return;

        throw new InvalidOperationException(
            $"{action}失敗：{string.Join(", ", result.Errors.Select(error => error.Description))}");
    }

    private static bool IsValidEmail(string email)
    {
        if (email.Length > 254 || !email.Contains('@', StringComparison.Ordinal))
            return false;

        try
        {
            var address = new System.Net.Mail.MailAddress(email);
            return string.Equals(address.Address, email, StringComparison.OrdinalIgnoreCase);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static async Task<DemoCredential> BuildCredentialAsync(
        UserManager<ApplicationUser> userManager,
        QmahDbContext db,
        ApplicationUser user,
        string password)
    {
        var nickname = await db.UserProfiles
            .AsNoTracking()
            .Where(profile => profile.UserId == user.Id)
            .Select(profile => profile.Nickname)
            .SingleOrDefaultAsync() ?? user.UserName ?? user.Email ?? "會員";
        var roles = await userManager.GetRolesAsync(user);
        var role = roles.FirstOrDefault(item => item.Equals("Admin", StringComparison.OrdinalIgnoreCase))
            ?? roles.FirstOrDefault()
            ?? "User";
        return new DemoCredential(nickname, user.Email ?? "", password, role);
    }

    private static void UpdateCredentialFiles(
        DemoCredential credential,
        string? credentialsPath,
        string? backupPath)
    {
        var localPath = ResolveCredentialsPath(credentialsPath);
        var backup = ResolveBackupPath(backupPath);
        var credentials = File.Exists(localPath)
            ? ReadCredentialFile(localPath)
            : File.Exists(backup)
                ? ReadCredentialFile(backup)
                : [];
        credentials.RemoveAll(item => item.Email.Equals(credential.Email, StringComparison.OrdinalIgnoreCase));
        credentials.Add(credential);
        WriteCredentialFiles(credentials, localPath, backup);
    }

    private static void WriteCredentialFiles(
        IEnumerable<DemoCredential> credentials,
        string? credentialsPath,
        string? backupPath)
    {
        var ordered = credentials
            .GroupBy(item => item.Email, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Last())
            .OrderBy(item => item.Email, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var content = new StringBuilder()
            .AppendLine("DisplayName,Email,Password,Role")
            .ToString();
        content += string.Join(
            Environment.NewLine,
            ordered.Select(item => string.Join(
                ',',
                Csv(item.DisplayName),
                Csv(item.Email),
                Csv(item.Password),
                Csv(item.Role))));
        content += Environment.NewLine;

        var localPath = ResolveCredentialsPath(credentialsPath);
        var backup = ResolveBackupPath(backupPath);
        WriteTextAtomically(localPath, content);
        if (!string.Equals(localPath, backup, StringComparison.OrdinalIgnoreCase))
            WriteTextAtomically(backup, content);
    }

    private static List<DemoCredential> ReadCredentialFile(string path) =>
        File.ReadAllLines(path)
            .Skip(1)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(ParseCredentialLine)
            .ToList();

    private static DemoCredential ParseCredentialLine(string line)
    {
        var values = ParseCsv(line);
        if (values.Count != 4)
            throw new InvalidDataException($"Credential CSV 欄位數不正確：{line}");
        return new DemoCredential(values[0], values[1], values[2], values[3]);
    }

    private static List<string> ParseCsv(string line)
    {
        var values = new List<string>();
        var value = new StringBuilder();
        var quoted = false;
        for (var index = 0; index < line.Length; index++)
        {
            var character = line[index];
            if (character == '"')
            {
                if (quoted && index + 1 < line.Length && line[index + 1] == '"')
                {
                    value.Append('"');
                    index++;
                }
                else
                {
                    quoted = !quoted;
                }
            }
            else if (character == ',' && !quoted)
            {
                values.Add(value.ToString());
                value.Clear();
            }
            else
            {
                value.Append(character);
            }
        }

        if (quoted)
            throw new InvalidDataException("Credential CSV 包含未關閉的引號。");
        values.Add(value.ToString());
        return values;
    }

    private static string Csv(string value) =>
        value.IndexOfAny([',', '"', '\r', '\n']) >= 0
            ? $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\""
            : value;

    private static void WriteTextAtomically(string path, string content)
    {
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var temporary = fullPath + $".{Guid.NewGuid():N}.tmp";
        File.WriteAllText(temporary, content, new UTF8Encoding(false));
        File.Move(temporary, fullPath, overwrite: true);
    }

    private static string ResolveCredentialsPath(string? path) =>
        Path.GetFullPath(string.IsNullOrWhiteSpace(path)
            ? Path.Combine(ResolveLegacyRepositoryParent(), "QMAH.DemoCredentials.local.csv")
            : path);

    private static string ResolveBackupPath(string? path) =>
        Path.GetFullPath(string.IsNullOrWhiteSpace(path)
            ? Path.Combine(ResolveLegacyRepositoryParent(), "QMAH.DemoCredentials.local.backup.csv")
            : path);

    private static string ResolveRepositoryRoot()
    {
        // 預設從 Repository 根目錄讀取，方便 VS 與命令列共用同一份設定
        var startPaths = new[]
        {
            Directory.GetCurrentDirectory(),
            AppContext.BaseDirectory
        };

        foreach (var startPath in startPaths)
        {
            var directory = new DirectoryInfo(Path.GetFullPath(startPath));
            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "QMAH.sln"))
                    || File.Exists(Path.Combine(directory.FullName, "QMAH.DatabaseTools.sln")))
                    return directory.FullName;

                directory = directory.Parent;
            }
        }

        throw new InvalidOperationException(
            "找不到 QMAH Repository 根目錄，請明確指定 --credentials 與 --backup。 ");
    }

    private static string ResolveLegacyRepositoryParent()
    {
        var parent = Directory.GetParent(ResolveRepositoryRoot())?.FullName;
        return parent
            ?? throw new InvalidOperationException("找不到 Repository 上一層，無法讀取舊版帳密檔。 ");
    }

    private static Dictionary<string, string> LoadShowcasePasswords(
        string? credentialsPath,
        string? backupPath)
    {
        var candidatePaths = new[]
            {
                ResolveCredentialsPath(credentialsPath),
                ResolveBackupPath(backupPath)
            }
            .Concat(string.IsNullOrWhiteSpace(credentialsPath) && string.IsNullOrWhiteSpace(backupPath)
                ? new[]
                {
                    Path.Combine(ResolveLegacyRepositoryParent(), "QMAH.DemoCredentials.local.csv"),
                    Path.Combine(ResolveLegacyRepositoryParent(), "QMAH.DemoCredentials.local.backup.csv")
                }
                : Array.Empty<string>())
            .Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var path in candidatePaths)
        {
            if (!File.Exists(path))
                continue;

            return ReadCredentialFile(path)
                .Where(item => !string.IsNullOrWhiteSpace(item.Email))
                .GroupBy(item => item.Email, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => group.Last().Password,
                    StringComparer.OrdinalIgnoreCase);
        }

        throw new FileNotFoundException(
            "找不到展示帳密檔，請將根目錄的 QMAH.DemoCredentials.csv 複製為 QMAH.DemoCredentials.local.csv，填入所有 Password 後再執行。 ");
    }

    private static void EnsureShowcasePasswords(
        IReadOnlyDictionary<string, string> passwords)
    {
        var incomplete = ShowcaseUsers
            .Where(seed => !passwords.TryGetValue(seed.Email, out var password)
                || string.IsNullOrWhiteSpace(password))
            .Select(seed => seed.Email)
            .ToArray();
        if (incomplete.Length == 0)
            return;

        throw new InvalidDataException(
            $"展示帳密檔缺少或留白的 Password：{string.Join(", ", incomplete)}。請填妥後再執行，不會自動產生密碼。 ");
    }

    private static string RequireStoredPassword(
        IReadOnlyDictionary<string, string> passwords,
        string email)
    {
        if (passwords.TryGetValue(email, out var password)
            && !string.IsNullOrWhiteSpace(password))
            return password;

        throw new InvalidDataException(
            $"帳密檔缺少或留白的 Password：{email}。請填妥後再執行，或明確傳入 --password。 ");
    }

    private static async Task EnsureShowcaseAddressesAsync(
        QmahDbContext db,
        IReadOnlyList<ShowcaseSeededUser> seededUsers)
    {
        // 每位展示會員維持一筆預設地址，管理員使用資展國際，其餘使用台北捷運站資料
        var userIds = seededUsers.Select(item => item.User.Id).ToArray();
        var addresses = await db.UserAddresses
            .Where(address => userIds.Contains(address.UserId))
            .ToListAsync();

        foreach (var item in seededUsers)
        {
            var addressSeed = item.Index == 0
                ? ShowcaseAdminAddress
                : TaipeiMetroAddresses[(item.Index - 1) % TaipeiMetroAddresses.Count];
            // 優先沿用既有地址，避免展示批次每次重跑都新增一筆
            var address = addresses
                .Where(candidate => candidate.UserId == item.User.Id)
                .OrderByDescending(candidate => candidate.IsDefault)
                .ThenBy(candidate => candidate.CreatedAt)
                .FirstOrDefault();

            if (address is null)
            {
                address = new UserAddress
                {
                    Id = StableGuid($"showcase-address:{item.Seed.Email}"),
                    UserId = item.User.Id,
                    CreatedAt = item.User.CreatedAt
                };
                db.UserAddresses.Add(address);
            }

            foreach (var other in addresses.Where(candidate => candidate.UserId == item.User.Id))
                other.IsDefault = false;

            address.AddressLabel = item.Index == 0 ? "專題展示據點" : "常用地點";
            address.RecipientName = item.Seed.DisplayName;
            address.RecipientPhone = addressSeed.Phone;
            address.PostalCode = addressSeed.PostalCode;
            address.City = addressSeed.City;
            address.District = addressSeed.District;
            address.AddressLine = addressSeed.AddressLine;
            address.Latitude = addressSeed.Latitude;
            address.Longitude = addressSeed.Longitude;
            address.IsDefault = true;
            address.UpdatedAt = DateTime.UtcNow;
        }
    }

    private static async Task EnsureShowcaseAchievementsAsync(
        QmahDbContext db,
        IReadOnlyList<ShowcaseSeededUser> seededUsers)
    {
        // 只補缺少的成就，固定識別值讓展示批次可以重跑
        var achievements = await db.Achievements
            .AsNoTracking()
            .Where(achievement => achievement.Status == "ACTIVE")
            .OrderBy(achievement => achievement.Code)
            .ToListAsync();
        if (achievements.Count == 0)
            return;

        var userIds = seededUsers.Select(item => item.User.Id).ToArray();
        var existing = await db.UserAchievements
            .Where(item => userIds.Contains(item.UserId))
            .ToListAsync();
        var existingKeys = existing
            .Select(item => $"{item.UserId:N}:{item.AchievementId:N}")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var now = DateTime.UtcNow;

        foreach (var item in seededUsers)
        {
            var achievementCount = Math.Min(1 + item.Index % 3, achievements.Count);
            var availableDays = Math.Max(0, (now.Date - item.User.CreatedAt.Date).Days);
            for (var offset = 0; offset < achievementCount; offset++)
            {
                var achievement = achievements[(item.Index * 2 + offset) % achievements.Count];
                var key = $"{item.User.Id:N}:{achievement.Id:N}";
                if (!existingKeys.Add(key))
                    continue;

                var achievedAt = item.User.CreatedAt.Date
                    .AddDays(Math.Min(availableDays, 2 + item.Index + offset))
                    .AddHours(9 + offset);
                if (achievedAt >= now)
                    achievedAt = now.AddMinutes(-1);

                var isDisplayed = offset == 0 || item.Index % 4 == 0;
                DateTime? displayedAt = isDisplayed
                    ? (achievedAt.AddMinutes(30) < now ? achievedAt.AddMinutes(30) : now)
                    : null;
                db.UserAchievements.Add(new UserAchievement
                {
                    Id = StableGuid($"showcase-achievement:{item.Seed.Email}:{achievement.Code}"),
                    UserId = item.User.Id,
                    AchievementId = achievement.Id,
                    AchievedAt = achievedAt,
                    IsDisplayed = isDisplayed,
                    DisplayedAt = displayedAt
                });
            }
        }
    }

    private static Guid StableGuid(string value)
    {
        // 不另建流水號表，固定文字雜湊會產生相同 Guid，重跑時能辨識同一筆資料
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return new Guid(bytes.AsSpan(0, 16));
    }

    private sealed record DemoCredential(string DisplayName, string Email, string Password, string Role);

    private sealed record ShowcaseSeededUser(
        ApplicationUser User,
        ShowcaseUserSeed Seed,
        int Index);

    private sealed record ShowcaseAddressSeed(
        string PostalCode,
        string City,
        string District,
        string AddressLine,
        decimal Latitude,
        decimal Longitude,
        string Phone);

    private sealed record ShowcaseUserSeed(
        string DisplayName,
        string Email,
        string Role,
        int DaysAgo,
        string Bio);

    private static readonly IReadOnlyList<ShowcaseUserSeed> ShowcaseUsers =
    [
        new("Demo Admin", "admin@qmah.local", "Admin", 120, "負責整理專題展示環境與後台資料。"),
        new("Demo Member 01", "user@qmah.local", "User", 95, "喜歡從器形與材質開始認識文物。"),
        new("Demo Catalog", "catalog@qmah.local", "User", 88, "整理館藏資料，也歡迎分享不同角度的觀察。"),
        new("Demo Game Host", "game@qmah.local", "User", 78, "把每一回合的鑑定遊戲整理成容易回看的紀錄。"),
        new("Demo Social Editor", "social@qmah.local", "User", 70, "協助大家找到適合交流的主題與活動。"),
        new("Demo Store Editor", "store@qmah.local", "User", 64, "維護展示商品、庫存與訂單狀態。"),
        new("Demo Player 01", "player-a@qmah.local", "User", 52, "把每次遊戲都當成一次觀察練習。"),
        new("Demo Player 02", "player-b@qmah.local", "User", 44, "記錄在博物館裡遇到的細節。"),
        new("Demo Member 03", "demo.member03@qmah.test", "User", 42, "喜歡比較不同時代的釉色與器形。"),
        new("Demo Member 04", "demo.member04@qmah.test", "User", 38, "週末會把看展時記下的問題整理成筆記。"),
        new("Demo Member 05", "demo.member05@qmah.test", "User", 35, "對玉器與小型配件的工藝特別有興趣。"),
        new("Demo Member 06", "demo.member06@qmah.test", "User", 31, "正在練習從紋飾判斷作品可能的時代。"),
        new("Demo Member 07", "demo.member07@qmah.test", "User", 28, "喜歡把展場導覽內容和圖鑑資料交叉閱讀。"),
        new("Demo Member 08", "demo.member08@qmah.test", "User", 24, "最近開始收集自己看過的陶瓷作品。"),
        new("Demo Member 09", "demo.member09@qmah.test", "User", 21, "會先看尺寸與材質，再回頭讀完整說明。"),
        new("Demo Member 10", "demo.member10@qmah.test", "User", 18, "喜歡和朋友一起參加線上文物活動。"),
        new("Demo Member 11", "demo.member11@qmah.test", "User", 15, "把每次猜錯的題目當成下一次查資料的入口。"),
        new("Demo Member 12", "demo.member12@qmah.test", "User", 12, "對畫作中的人物配置與留白很有感覺。"),
        new("Demo Member 13", "demo.member13@qmah.test", "User", 10, "會把有趣的故宮編號記在自己的清單裡。"),
        new("Demo Member 14", "demo.member14@qmah.test", "User", 8, "喜歡在活動留言中交換看展路線。"),
        new("Demo Member 15", "demo.member15@qmah.test", "User", 6, "剛開始接觸故宮開放資料與圖像授權。"),
        new("Demo Member 16", "demo.member16@qmah.test", "User", 4, "喜歡研究不同材質在光線下的差異。"),
        new("Demo Member 17", "demo.member17@qmah.test", "User", 2, "期待在遊戲房間裡認識更多同好。"),
        new("Demo Member 18", "demo.member18@qmah.test", "User", 1, "把第一次參與的活動心得留在社群裡。")
    ];

    // 路徑順序要和 ShowcaseUsers 一一對齊，調整名單時也要同步調整頭貼
    private static readonly IReadOnlyList<string> ShowcaseAvatarPaths =
    [
        "/images/avatars/flat-icon-design/panda.png",
        "/images/avatars/flat-icon-design/deer.png",
        "/images/avatars/flat-icon-design/monkey.png",
        "/images/avatars/flat-icon-design/duck.png",
        "/images/avatars/flat-icon-design/lion.png",
        "/images/avatars/flat-icon-design/cat.png",
        "/images/avatars/flat-icon-design/dog.png",
        "/images/avatars/flat-icon-design/bird.png",
        "/images/avatars/flat-icon-design/tanuki.png",
        "/images/avatars/flat-icon-design/wolf.png",
        "/images/avatars/flat-icon-design/hippo.png",
        "/images/avatars/flat-icon-design/fox.png",
        "/images/avatars/flat-icon-design/buffalo.png",
        "/images/avatars/flat-icon-design/chicken.png",
        "/images/avatars/flat-icon-design/bull.png",
        "/images/avatars/flat-icon-design/seal.png",
        "/images/avatars/flat-icon-design/ladybug.png",
        "/images/avatars/flat-icon-design/goldfish.png",
        "/images/avatars/flat-icon-design/koala.png",
        "/images/avatars/flat-icon-design/bear.png",
        "/images/avatars/flat-icon-design/pig.png",
        "/images/avatars/flat-icon-design/elephant.png",
        "/images/avatars/flat-icon-design/giraffe.png",
        "/images/avatars/flat-icon-design/cow.png"
    ];

    private static readonly ShowcaseAddressSeed ShowcaseAdminAddress =
        new("106", "臺北市", "大安區", "復興南路一段 390 號 2 樓", 25.041122m, 121.543493m, "02-6631-6588");

    private static readonly IReadOnlyList<ShowcaseAddressSeed> TaipeiMetroAddresses =
    [
        new("100009", "臺北市", "中正區", "忠孝西路 1 段 49 號", 25.047825m, 121.517081m, "02-2181-2345"),
        new("106084", "臺北市", "大安區", "忠孝東路 3 段 302 號", 25.041778m, 121.544022m, "02-2181-2345"),
        new("106097", "臺北市", "大安區", "信義路 4 段 2 號", 25.033303m, 121.543531m, "02-2181-2345"),
        new("105020", "臺北市", "松山區", "南京東路 3 段 253 號", 25.051856m, 121.544098m, "02-2181-2345"),
        new("100005", "臺北市", "中正區", "寶慶路 32 之 1 號 B1", 25.042169m, 121.508276m, "02-2181-2345"),
        new("106007", "臺北市", "大安區", "信義路 2 段 166 號 B1", 25.033327m, 121.528049m, "02-2181-2345"),
        new("100207", "臺北市", "中正區", "羅斯福路 1 段 8 之 1 號 B1", 25.032729m, 121.518270m, "02-2181-2345"),
        new("100046", "臺北市", "中正區", "羅斯福路 4 段 64 之 1 號 B1", 25.014392m, 121.534027m, "02-2181-2345"),
        new("100024", "臺北市", "中正區", "忠孝東路 1 段 58 號 B1", 25.044739m, 121.523177m, "02-2181-2345"),
        new("103014", "臺北市", "大同區", "南京西路 16 號", 25.052009m, 121.520175m, "02-2181-2345"),
        new("106083", "臺北市", "大安區", "新生南路 1 段 67 號", 25.042452m, 121.532472m, "02-2181-2345"),
        new("106101", "臺北市", "大安區", "復興南路 2 段 235 號", 25.026050m, 121.543521m, "02-2181-2345"),
        new("106057", "臺北市", "大安區", "忠孝東路 4 段 182 號", 25.041841m, 121.551013m, "02-2181-2345"),
        new("110054", "臺北市", "信義區", "忠孝東路 4 段 400 號", 25.041746m, 121.560228m, "02-2181-2345"),
        new("110060", "臺北市", "信義區", "忠孝東路 5 段 2 號", 25.040858m, 121.565908m, "02-2181-2345"),
        new("100043", "臺北市", "中正區", "羅斯福路 3 段 126 之 5 號 B1", 25.020112m, 121.528215m, "02-2181-2345"),
        new("116060", "臺北市", "文山區", "羅斯福路 5 段 214 號", 24.999030m, 121.539326m, "02-2181-2345")
    ];
}
