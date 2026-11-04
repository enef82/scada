using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.HttpOverrides;
using Npgsql;
using Scada.Core.Data;
using Scada.Web.Api;
using Scada.Web.Infrastructure;
using Scada.Web.Realtime;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSystemd();
builder.Configuration.AddEnvironmentVariables("SCADA_");

var connectionString = builder.Configuration.GetConnectionString("Scada")
    ?? throw new InvalidOperationException("Не задана строка подключения ConnectionStrings:Scada");
builder.Services.AddSingleton(_ => Db.CreateDataSource(connectionString, "scada-web"));

builder.Services.Configure<CommandOptions>(builder.Configuration.GetSection("Commands"));
builder.Services.Configure<RealtimeOptions>(builder.Configuration.GetSection("Realtime"));
builder.Services.Configure<PublishOptions>(builder.Configuration.GetSection("Publishing"));
builder.Services.AddSingleton<PublishService>();

builder.Services.AddSingleton<Passwords>();
builder.Services.AddSingleton<LoginThrottle>();
builder.Services.AddSingleton<Audit>();
builder.Services.AddSingleton<CommandWaiter>();
builder.Services.AddSingleton<RealtimeService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<RealtimeService>());

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(o =>
    {
        o.Cookie.Name = "scada.auth";
        o.Cookie.HttpOnly = true;
        o.Cookie.SameSite = SameSiteMode.Strict;
        o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest; // за HTTPS-прокси — Always
        o.ExpireTimeSpan = TimeSpan.FromHours(12);                // смена оператора
        o.SlidingExpiration = true;
        // API: 401/403 вместо редиректа на страницу входа
        o.Events.OnRedirectToLogin = ctx => { ctx.Response.StatusCode = 401; return Task.CompletedTask; };
        o.Events.OnRedirectToAccessDenied = ctx => { ctx.Response.StatusCode = 403; return Task.CompletedTask; };
    });
builder.Services.AddAuthorization(Roles.AddPolicies);

builder.Services.AddSignalR(o =>
{
    o.MaximumReceiveMessageSize = 256 * 1024;
    o.KeepAliveInterval = TimeSpan.FromSeconds(10);
    o.ClientTimeoutInterval = TimeSpan.FromSeconds(30);
});
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.Never;
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});
builder.Services.AddProblemDetails();
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    o.KnownProxies.Add(System.Net.IPAddress.Loopback);
});

var app = builder.Build();

app.UseForwardedHeaders();
app.UseExceptionHandler();
app.UseStatusCodePages();

// заголовки безопасности
app.Use(async (ctx, next) =>
{
    var h = ctx.Response.Headers;
    h["X-Content-Type-Options"] = "nosniff";
    h["X-Frame-Options"] = "DENY";
    h["Referrer-Policy"] = "no-referrer";
    h["Content-Security-Policy"] =
        "default-src 'self'; img-src 'self' data:; style-src 'self' 'unsafe-inline'; connect-src 'self' ws: wss:; frame-ancestors 'none'";
    await next();
});

// защита от CSRF: изменяющие запросы API обязаны нести собственный заголовок
// (браузер не отправит его с чужого сайта без CORS, а CORS не разрешён)
app.Use(async (ctx, next) =>
{
    if (ctx.Request.Path.StartsWithSegments("/api")
        && !HttpMethods.IsGet(ctx.Request.Method) && !HttpMethods.IsHead(ctx.Request.Method)
        && !ctx.Request.Headers.ContainsKey("X-Scada-Request"))
    {
        ctx.Response.StatusCode = 400;
        await ctx.Response.WriteAsJsonAsync(new { title = "Отсутствует заголовок X-Scada-Request" });
        return;
    }
    await next();
});

app.UseDefaultFiles();
app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();

var api = app.MapGroup("/api").RequireAuthorization(Roles.CanView);
AuthApi.Map(api);
ConfigApi.Map(api);
RuntimeApi.Map(api);
MnemoApi.Map(api);
AdminApi.Map(api);
PublishApi.Map(api);

app.MapGet("/health", async (NpgsqlDataSource ds, RealtimeService rt, CancellationToken ct) =>
{
    bool dbOk;
    try { dbOk = await ds.ScalarAsync("SELECT 1", ct) is not null; } catch { dbOk = false; }
    var age = rt.LastHeartbeatAt is { } t ? (DateTime.UtcNow - t).TotalSeconds : (double?)null;
    var acqOk = age is < 20;
    var body = new { status = dbOk && acqOk ? "ok" : "degraded", db = dbOk, listener = rt.ListenerConnected, acquisitionAgeSec = age };
    return dbOk ? Results.Ok(body) : Results.Json(body, statusCode: 503);
}).AllowAnonymous();

app.MapHub<RealtimeHub>("/hubs/realtime");
app.Map("/api/{**rest}", () => Results.NotFound()).AllowAnonymous(); // неизвестный API — 404, а не index.html
app.MapFallbackToFile("index.html");

// первый запуск: создать администратора (ждём БД, если она ещё стартует)
var log = app.Services.GetRequiredService<ILogger<Program>>();
for (var attempt = 1; ; attempt++)
{
    try
    {
        await AuthApi.EnsureAdminAsync(app.Services.GetRequiredService<NpgsqlDataSource>(),
            app.Services.GetRequiredService<Passwords>(),
            builder.Configuration["Scada:InitialAdminPassword"] ?? "admin12345", log);
        await Scada.Core.Config.DraftStore.EnsureInitialVersionAsync(app.Services.GetRequiredService<NpgsqlDataSource>(), CancellationToken.None);
        break;
    }
    catch (Exception ex) when (attempt < 30)
    {
        log.LogWarning("БД недоступна ({Error}), повтор {Attempt}/30", ex.Message, attempt);
        await Task.Delay(TimeSpan.FromSeconds(2));
    }
}

app.Run();
