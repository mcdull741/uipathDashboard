using UiPathJobManager.Data;
using UiPathJobManager.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Authentication.Cookies;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddControllersWithViews();

// 数据库
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("DefaultConnection")));

// 认证
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/Account/Login";
        options.AccessDeniedPath = "/Account/Login";
        options.ExpireTimeSpan = TimeSpan.FromHours(8);
        options.SlidingExpiration = true;
        options.Cookie.Name = "UiPathJobAuth";
    });

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("AdminOnly", policy => policy.RequireRole("Admin"));
});

// HttpClient 用于调用 UiPath Orchestrator API
builder.Services.AddHttpClient("UiPath", client =>
{
    client.Timeout = TimeSpan.FromSeconds(30);
});

// 绑定 appsettings.json 的 "UiPath" 节
builder.Services.Configure<UiPathOptions>(builder.Configuration.GetSection("UiPath"));

// Token 缓存为 Singleton，跨作用域复用同一张票据
builder.Services.AddSingleton<IUiPathTokenProvider, UiPathTokenProvider>();

// 文件夹树缓存为 Singleton，避免每次同步都拉取 /odata/Folders
builder.Services.AddSingleton<IUiPathFolderService, UiPathFolderService>();

// 应用服务
builder.Services.AddScoped<IAuthService, AuthService>();
builder.Services.AddScoped<IUiPathApiService, UiPathApiService>();
builder.Services.AddScoped<IJobSyncService, JobSyncService>();
builder.Services.AddHostedService<JobSyncBackgroundService>();

var app = builder.Build();

// 数据库初始化
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.EnsureCreated();
    await DbSeeder.SeedAsync(db);
}

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Dashboard}/{action=Index}/{id?}");

app.Run();