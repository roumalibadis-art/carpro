using System.Text;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using Prospecta.Application;
using Prospecta.Application.Abstractions;
using Prospecta.Application.Security;
using Prospecta.Infrastructure;
using Prospecta.Infrastructure.Seeding;
using Prospecta.Web.Middleware;
using Prospecta.Web.Security;
using Serilog;

var builder = WebApplication.CreateBuilder(args);
builder.Host.UseSerilog((ctx, lc) => lc.ReadFrom.Configuration(ctx.Configuration).WriteTo.Console());

var config = builder.Configuration;
var jwt = config.GetSection("Jwt").Get<JwtOptions>() ?? new JwtOptions();
if (string.IsNullOrWhiteSpace(jwt.Secret) || jwt.Secret.Length < 32)
{
    if (!builder.Environment.IsDevelopment()) throw new InvalidOperationException("Jwt:Secret (32 caractères minimum) doit être fourni par variable d'environnement ou gestionnaire de secrets.");
    jwt.Secret = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)); // ephemeral dev key
    Console.WriteLine("WARN Jwt:Secret absent : clé éphémère générée (les jetons expirent au redémarrage).");
}

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.Configure<JwtOptions>(o => { o.Secret = jwt.Secret; o.Issuer = jwt.Issuer; o.Audience = jwt.Audience; o.ExpiresMinutes = jwt.ExpiresMinutes; });
builder.Services.AddHttpContextAccessor();
// Render accented French and Arabic text as-is (still HTML-encoded for markup characters).
builder.Services.AddWebEncoders(o => o.TextEncoderSettings = new System.Text.Encodings.Web.TextEncoderSettings(System.Text.Unicode.UnicodeRanges.BasicLatin, System.Text.Unicode.UnicodeRanges.Latin1Supplement, System.Text.Unicode.UnicodeRanges.LatinExtendedA, System.Text.Unicode.UnicodeRanges.Arabic, System.Text.Unicode.UnicodeRanges.GeneralPunctuation));
builder.Services.AddScoped<ICurrentUser, HttpCurrentUser>();
builder.Services.AddScoped<JwtTokenService>();
builder.Services.AddScoped<LoginService>();
builder.Services.AddScoped<Prospecta.Web.Pages.UiLookups>();
builder.Services.AddInfrastructure(config);
builder.Services.AddApplication();
builder.Services.AddScoped<IClaimsTransformation, PermissionClaimsTransformation>();

builder.Services.AddAuthentication("Smart")
    .AddPolicyScheme("Smart", null, o => o.ForwardDefaultSelector = ctx => ctx.Request.Path.StartsWithSegments("/api")
        ? JwtBearerDefaults.AuthenticationScheme : CookieAuthenticationDefaults.AuthenticationScheme)
    .AddJwtBearer(o =>
    {
        o.MapInboundClaims = false;
        o.TokenValidationParameters = new TokenValidationParameters
        {
            ValidIssuer = jwt.Issuer, ValidAudience = jwt.Audience, ClockSkew = TimeSpan.FromSeconds(30),
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.Secret)),
            NameClaimType = System.Security.Claims.ClaimTypes.Name, RoleClaimType = System.Security.Claims.ClaimTypes.Role,
        };
        o.Events = new JwtBearerEvents
        {
            // Map the compact claim names back to the framework ones used by the rest of the app.
            OnTokenValidated = async ctx =>
            {
                if (!await ActiveUserCheck.IsValidAsync(ctx.Principal!, ctx.HttpContext.RequestServices.GetRequiredService<Prospecta.Infrastructure.Persistence.AppDbContext>()))
                {
                    ctx.Fail("Compte désactivé ou session révoquée.");
                    return;
                }

                var id = (System.Security.Claims.ClaimsIdentity)ctx.Principal!.Identity!;
                foreach (var c in id.FindAll("sub").ToList()) if (!id.HasClaim(x => x.Type == System.Security.Claims.ClaimTypes.NameIdentifier)) id.AddClaim(new(System.Security.Claims.ClaimTypes.NameIdentifier, c.Value));
            },
        };
    })
    .AddCookie(CookieAuthenticationDefaults.AuthenticationScheme, o =>
    {
        o.LoginPath = "/Account/Login";
        o.AccessDeniedPath = "/Account/AccessDenied";
        o.Cookie.Name = "prospecta.auth";
        o.Cookie.HttpOnly = true;
        o.Cookie.SameSite = SameSiteMode.Lax;
        o.Cookie.SecurePolicy = builder.Environment.IsDevelopment() ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
        o.ExpireTimeSpan = TimeSpan.FromHours(8);
        o.SlidingExpiration = true;
        o.Events.OnValidatePrincipal = async ctx =>
        {
            if (!await ActiveUserCheck.IsValidAsync(ctx.Principal!, ctx.HttpContext.RequestServices.GetRequiredService<Prospecta.Infrastructure.Persistence.AppDbContext>()))
            {
                ctx.RejectPrincipal();
                await ctx.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            }
        };
    });

builder.Services.AddAuthorization(o =>
{
    foreach (var p in Permissions.All) o.AddPolicy(p, pol => pol.RequireAuthenticatedUser().RequireClaim(Permissions.ClaimType, p));
    o.FallbackPolicy = new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();
});

builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = 429;
    // Only credential submissions are throttled; viewing the login page (e.g. after a session expires) is never blocked.
    o.AddPolicy("login", ctx => !HttpMethods.IsPost(ctx.Request.Method)
        ? RateLimitPartition.GetNoLimiter("view")
        : RateLimitPartition.GetFixedWindowLimiter(ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions { PermitLimit = ctx.RequestServices.GetRequiredService<IConfiguration>().GetValue("RateLimit:LoginPerMinute", 10), Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});

builder.Services.AddControllers().AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()))
    .ConfigureApiBehaviorOptions(o => o.InvalidModelStateResponseFactory = ctx => new Microsoft.AspNetCore.Mvc.BadRequestObjectResult(new
    {
        success = false, message = "Données invalides.",
        errors = ctx.ModelState.Values.SelectMany(v => v.Errors).Select(e => string.IsNullOrEmpty(e.ErrorMessage) ? "Valeur invalide." : e.ErrorMessage).ToArray(),
    }));
builder.Services.AddRazorPages(o => { o.Conventions.AllowAnonymousToPage("/Account/Login"); o.Conventions.AllowAnonymousToPage("/Account/AccessDenied"); o.Conventions.AllowAnonymousToPage("/Error"); });
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(o =>
{
    o.SwaggerDoc("v1", new OpenApiInfo { Title = "Prospecta API", Version = "v1" });
    o.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme { Type = SecuritySchemeType.Http, Scheme = "bearer", BearerFormat = "JWT" });
    o.AddSecurityRequirement(new OpenApiSecurityRequirement { [new OpenApiSecurityScheme { Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" } }] = [] });
});
builder.Services.AddHostedService<Prospecta.Web.Security.ReminderHostedService>();
builder.Services.AddHealthChecks().AddDbContextCheck<Prospecta.Infrastructure.Persistence.AppDbContext>();
builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(o => o.MultipartBodyLengthLimit = 6 * 1024 * 1024);

builder.WebHost.ConfigureKestrel(k => k.AddServerHeader = false); // do not advertise the server software

var app = builder.Build();

Directory.CreateDirectory(Path.Combine(app.Environment.ContentRootPath, "App_Data"));

if (config.GetValue("Database:AutoMigrate", app.Environment.IsDevelopment())) await DataSeeder.RunAsync(app.Services);

if (config.GetValue<bool>("ForwardedHeaders:Enabled")) app.UseForwardedHeaders(new() { ForwardedHeaders = Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedFor | Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedProto });
app.UseSerilogRequestLogging();
app.UseMiddleware<SecurityHeadersMiddleware>();
app.UseMiddleware<ErrorHandlingMiddleware>();
if (!app.Environment.IsDevelopment()) { app.UseExceptionHandler("/Error"); app.UseHsts(); app.UseHttpsRedirection(); }
app.UseStaticFiles();
app.UseRouting();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

app.MapHealthChecks("/health").AllowAnonymous();
if (app.Environment.IsDevelopment() || config.GetValue<bool>("Swagger:Enabled")) { app.UseSwagger(); app.UseSwaggerUI(); }
app.MapControllers();
app.MapRazorPages();
app.MapUiEndpoints();
app.Run();

public partial class Program;
