using Microsoft.EntityFrameworkCore;
using OpenDeepWiki.EFCore;
using OpenDeepWiki.Entities;
using OpenDeepWiki.Services.Admin;

namespace OpenDeepWiki.Infrastructure;

/// <summary>
/// 数据库初始化服务
/// </summary>
public static class DbInitializer
{
    /// <summary>
    /// 初始化数据库（创建默认角色和OAuth提供商）
    /// </summary>
    public static async Task InitializeAsync(IServiceProvider serviceProvider)
    {
        using var scope = serviceProvider.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<IContext>();
        var configuration = scope.ServiceProvider.GetRequiredService<IConfiguration>();

        // 确保数据库已创建
        if (context is DbContext dbContext)
        {
            await dbContext.Database.EnsureCreatedAsync();
        }

        // 初始化默认角色
        await InitializeRolesAsync(context);

        // 初始化默认管理员账户
        await InitializeAdminUserAsync(context);

        // 初始化OAuth提供商
        await InitializeOAuthProvidersAsync(context);

        // Schema migrations for existing databases
        if (context is DbContext migrationCtx)
        {
            var isSqlite = migrationCtx.Database.ProviderName?.Contains("Sqlite",
                StringComparison.OrdinalIgnoreCase) == true;

            if (isSqlite)
            {
                await MigrateSqliteAsync(migrationCtx);
            }
            else
            {
                await MigratePostgresqlAsync(migrationCtx);
            }
        }

        // 初始化系统设置默认值（仅在首次运行时从环境变量创建）
        await SystemSettingDefaults.InitializeDefaultsAsync(configuration, context);

        await RefreshBundledSkillsAsync(scope.ServiceProvider);
    }

    private static async Task RefreshBundledSkillsAsync(IServiceProvider serviceProvider)
    {
        try
        {
            var toolsService = serviceProvider.GetRequiredService<IAdminToolsService>();
            await toolsService.RefreshSkillsFromDiskAsync();
        }
        catch (Exception ex)
        {
            var logger = serviceProvider.GetService<ILoggerFactory>()?.CreateLogger("DbInitializer");
            logger?.LogWarning(ex, "Failed to refresh bundled skills from disk");
        }
    }

    private static async Task InitializeAdminUserAsync(IContext context)
    {
        const string adminEmail = "admin@routin.ai";
        const string adminPassword = "Admin@123";

        var exists = await context.Users.AnyAsync(u => u.Email == adminEmail && !u.IsDeleted);
        if (exists) return;

        var adminRole = await context.Roles.FirstOrDefaultAsync(r => r.Name == "Admin" && !r.IsDeleted);
        if (adminRole == null) return;

        var adminUser = new User
        {
            Id = Guid.NewGuid().ToString(),
            Name = "admin",
            Email = adminEmail,
            Password = BCrypt.Net.BCrypt.HashPassword(adminPassword),
            Status = 1,
            IsSystem = true,
            CreatedAt = DateTime.UtcNow
        };

        context.Users.Add(adminUser);

        var userRole = new UserRole
        {
            Id = Guid.NewGuid().ToString(),
            UserId = adminUser.Id,
            RoleId = adminRole.Id,
            CreatedAt = DateTime.UtcNow
        };

        context.UserRoles.Add(userRole);
        await context.SaveChangesAsync();
    }

    private static async Task InitializeRolesAsync(IContext context)
    {
        var roles = new[]
        {
            new Role
            {
                Id = Guid.NewGuid().ToString(),
                Name = "Admin",
                Description = "系统管理员",
                IsActive = true,
                IsSystemRole = true,
                CreatedAt = DateTime.UtcNow
            },
            new Role
            {
                Id = Guid.NewGuid().ToString(),
                Name = "User",
                Description = "普通用户",
                IsActive = true,
                IsSystemRole = true,
                CreatedAt = DateTime.UtcNow
            }
        };

        foreach (var role in roles)
        {
            var exists = await context.Roles.AnyAsync(r => r.Name == role.Name && !r.IsDeleted);
            if (!exists)
            {
                context.Roles.Add(role);
            }
        }

        await context.SaveChangesAsync();
    }

    private static async Task InitializeOAuthProvidersAsync(IContext context)
    {
        var providers = new[]
        {
            new OAuthProvider
            {
                Id = Guid.NewGuid().ToString(),
                Name = "github",
                DisplayName = "GitHub",
                AuthorizationUrl = "https://github.com/login/oauth/authorize",
                TokenUrl = "https://github.com/login/oauth/access_token",
                UserInfoUrl = "https://api.github.com/user",
                ClientId = "YOUR_GITHUB_CLIENT_ID",
                ClientSecret = "YOUR_GITHUB_CLIENT_SECRET",
                RedirectUri = "http://localhost:8080/api/oauth/github/callback",
                Scope = "user:email",
                UserInfoMapping = "{\"id\":\"id\",\"name\":\"login\",\"email\":\"email\",\"avatar\":\"avatar_url\"}",
                IsActive = false,
                CreatedAt = DateTime.UtcNow
            },
            new OAuthProvider
            {
                Id = Guid.NewGuid().ToString(),
                Name = "gitee",
                DisplayName = "Gitee",
                AuthorizationUrl = "https://gitee.com/oauth/authorize",
                TokenUrl = "https://gitee.com/oauth/token",
                UserInfoUrl = "https://gitee.com/api/v5/user",
                ClientId = "YOUR_GITEE_CLIENT_ID",
                ClientSecret = "YOUR_GITEE_CLIENT_SECRET",
                RedirectUri = "http://localhost:8080/api/oauth/gitee/callback",
                Scope = "user_info emails",
                UserInfoMapping = "{\"id\":\"id\",\"name\":\"name\",\"email\":\"email\",\"avatar\":\"avatar_url\"}",
                IsActive = false,
                CreatedAt = DateTime.UtcNow
            }
        };

        foreach (var provider in providers)
        {
            var exists = await context.OAuthProviders.AnyAsync(p => p.Name == provider.Name && !p.IsDeleted);
            if (!exists)
            {
                context.OAuthProviders.Add(provider);
            }
        }

        await context.SaveChangesAsync();
    }

    private static async Task MigrateSqliteAsync(DbContext ctx)
    {
        // Create ApiKeys table (SQLite types)
        await ctx.Database.ExecuteSqlRawAsync(@"
            CREATE TABLE IF NOT EXISTS ApiKeys (
                Id TEXT NOT NULL PRIMARY KEY,
                Name TEXT NOT NULL,
                KeyPrefix TEXT NOT NULL,
                KeyHash TEXT NOT NULL,
                UserId TEXT NOT NULL,
                Scope TEXT NOT NULL DEFAULT 'mcp:read',
                ExpiresAt TEXT,
                LastUsedAt TEXT,
                LastUsedIp TEXT,
                CreatedAt TEXT NOT NULL,
                UpdatedAt TEXT,
                DeletedAt TEXT,
                IsDeleted INTEGER NOT NULL DEFAULT 0,
                Version BLOB,
                FOREIGN KEY (UserId) REFERENCES Users(Id)
            )");
        await ctx.Database.ExecuteSqlRawAsync(
            "CREATE UNIQUE INDEX IF NOT EXISTS IX_ApiKeys_KeyPrefix ON ApiKeys (KeyPrefix)");
        await ctx.Database.ExecuteSqlRawAsync(
            "CREATE INDEX IF NOT EXISTS IX_ApiKeys_UserId ON ApiKeys (UserId)");

        // Add Description column if not exists
        var connection = ctx.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
            await connection.OpenAsync();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM pragma_table_info('Repositories') WHERE name='Description'";
        var result = await cmd.ExecuteScalarAsync();
        if (Convert.ToInt64(result) == 0)
        {
            await ctx.Database.ExecuteSqlRawAsync(
                "ALTER TABLE Repositories ADD COLUMN Description TEXT");
        }
    }

    private static async Task MigratePostgresqlAsync(DbContext ctx)
    {
        // Create ApiKeys table (PostgreSQL types)
        await ctx.Database.ExecuteSqlRawAsync(@"
            CREATE TABLE IF NOT EXISTS ""ApiKeys"" (
                ""Id"" TEXT NOT NULL PRIMARY KEY,
                ""Name"" TEXT NOT NULL,
                ""KeyPrefix"" TEXT NOT NULL,
                ""KeyHash"" TEXT NOT NULL,
                ""UserId"" TEXT NOT NULL,
                ""Scope"" TEXT NOT NULL DEFAULT 'mcp:read',
                ""ExpiresAt"" TIMESTAMP WITH TIME ZONE,
                ""LastUsedAt"" TIMESTAMP WITH TIME ZONE,
                ""LastUsedIp"" TEXT,
                ""CreatedAt"" TIMESTAMP WITH TIME ZONE NOT NULL,
                ""UpdatedAt"" TIMESTAMP WITH TIME ZONE,
                ""DeletedAt"" TIMESTAMP WITH TIME ZONE,
                ""IsDeleted"" BOOLEAN NOT NULL DEFAULT FALSE,
                ""Version"" BYTEA,
                FOREIGN KEY (""UserId"") REFERENCES ""Users""(""Id"")
            )");
        await ctx.Database.ExecuteSqlRawAsync(@"
            CREATE UNIQUE INDEX IF NOT EXISTS ""IX_ApiKeys_KeyPrefix"" ON ""ApiKeys"" (""KeyPrefix"")");
        await ctx.Database.ExecuteSqlRawAsync(@"
            CREATE INDEX IF NOT EXISTS ""IX_ApiKeys_UserId"" ON ""ApiKeys"" (""UserId"")");

        // Add Description column if not exists
        await ctx.Database.ExecuteSqlRawAsync(@"
            DO $$
            BEGIN
                IF NOT EXISTS (
                    SELECT 1 FROM information_schema.columns
                    WHERE table_name = 'Repositories' AND column_name = 'Description'
                ) THEN
                    ALTER TABLE ""Repositories"" ADD COLUMN ""Description"" TEXT;
                END IF;
            END $$;");
    }
}
