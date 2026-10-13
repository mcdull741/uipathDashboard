using Microsoft.EntityFrameworkCore;
using UiPathJobManager.Models;

namespace UiPathJobManager.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<User> Users => Set<User>();
    public DbSet<Job> Jobs => Set<Job>();
    public DbSet<AppConfig> AppConfigs => Set<AppConfig>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<User>(e =>
        {
            e.HasIndex(u => u.Username).IsUnique();
            e.Property(u => u.Role).HasDefaultValue("User");
        });

        modelBuilder.Entity<Job>(e =>
        {
            e.HasIndex(j => j.StartTime);
            e.HasIndex(j => j.State);
            e.HasIndex(j => j.ProcessName);
            e.HasIndex(j => new { j.StartTime, j.State });
        });

        modelBuilder.Entity<AppConfig>(e =>
        {
            e.HasIndex(c => c.ConfigKey).IsUnique();
        });
    }
}

public static class DbSeeder
{
    public static async Task SeedAsync(AppDbContext db)
    {
        // 默认管理员账号
        if (!db.Users.Any())
        {
            var adminUser = new User
            {
                Username = "admin",
                DisplayName = "系统管理员",
                Email = "admin@example.com",
                Role = "Admin",
                IsActive = true,
                CreatedAt = DateTime.UtcNow,
                PasswordHash = BCrypt.Net.BCrypt.HashPassword("Admin@123456")
            };
            db.Users.Add(adminUser);

            var demoUser = new User
            {
                Username = "demo",
                DisplayName = "演示用户",
                Email = "demo@example.com",
                Role = "User",
                IsActive = true,
                CreatedAt = DateTime.UtcNow,
                PasswordHash = BCrypt.Net.BCrypt.HashPassword("Demo@123")
            };
            db.Users.Add(demoUser);

            await db.SaveChangesAsync();
        }

        // 默认配置
        if (!db.AppConfigs.Any())
        {
            var defaults = new[]
            {
                new AppConfig { ConfigKey = "UiPath.BaseUrl", ConfigValue = "", Description = "UiPath Orchestrator 地址,例如 https://cloud.uipath.com" },
                new AppConfig { ConfigKey = "UiPath.TenantName", ConfigValue = "", Description = "租户名" },
                new AppConfig { ConfigKey = "UiPath.FolderId", ConfigValue = "", Description = "文件夹 ID" },
                new AppConfig { ConfigKey = "UiPath.ApiKey", ConfigValue = "", Description = "API Key (外部令牌)" },
                new AppConfig { ConfigKey = "UiPath.ClientId", ConfigValue = "", Description = "OAuth ClientId" },
                new AppConfig { ConfigKey = "UiPath.ClientSecret", ConfigValue = "", Description = "OAuth ClientSecret" },
                new AppConfig { ConfigKey = "UiPath.SyncIntervalMinutes", ConfigValue = "5", Description = "同步间隔 (分钟)" },
                new AppConfig { ConfigKey = "UiPath.PageSize", ConfigValue = "100", Description = "每次拉取页大小" },
                new AppConfig { ConfigKey = "UiPath.UseMockData", ConfigValue = "true", Description = "是否使用模拟数据 (true/false)" },
            };
            db.AppConfigs.AddRange(defaults);
            await db.SaveChangesAsync();
        }
    }
}