using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using FileUploader.Data;
using FileUploader.Services;
using FileUploader.BackgroundServices;
using FileUploader.Models;
using Microsoft.AspNetCore.HttpOverrides;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
});

// Add services to the container
builder.Services.AddRazorPages();
builder.Services.AddServerSideBlazor().AddHubOptions(options =>
{
    options.KeepAliveInterval = TimeSpan.FromSeconds(10);
    // Files are uploaded over plain HTTP, never over the SignalR circuit, so keep the hub
    // message limit small. A huge limit lets any client push 100+ MB messages into server memory.
    options.MaximumReceiveMessageSize = 64 * 1024;
});
builder.Services.AddControllers();

// Comma-separated, e.g. "https://a.example,https://b.example". Empty means same-origin only.
var corsOrigins = (builder.Configuration["Cors:AllowedOrigins"] ?? string.Empty)
    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
    .ToList();
if (builder.Environment.IsDevelopment())
{
    corsOrigins.AddRange(new[] { "http://localhost:5154", "http://localhost:5000" });
}

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.WithOrigins(corsOrigins.ToArray())
              .WithMethods("GET", "POST")
              .AllowAnyHeader();
    });
});

// Configure PostgreSQL
builder.Services.AddDbContext<ApplicationDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection")));

builder.Services.Configure<InstanceOptions>(builder.Configuration.GetSection(InstanceOptions.SectionName));

// Register custom services
builder.Services.AddScoped<FileValidationService>();
builder.Services.AddScoped<ShortCodeService>();
builder.Services.AddSingleton<MetadataStripperService>();
builder.Services.AddSingleton<FileStorageService>();

// Register background services
builder.Services.AddHostedService<FileCleanupService>();

// Configure file upload limits
builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(options =>
{
    var maxFileSizeBytes = FileSizeModel.MaxFileSizeBytes;
    options.MultipartBodyLengthLimit = maxFileSizeBytes; // 100 MB
});

builder.Services.Configure<IISServerOptions>(options =>
{
    var maxFileSizeBytes = FileSizeModel.MaxFileSizeBytes;
    options.MaxRequestBodySize = maxFileSizeBytes; // 100 MB
});

builder.Services.Configure<KestrelServerOptions>(options =>
{
    // Leave room for the multipart envelope around a max-size file.
    options.Limits.MaxRequestBodySize = FileSizeModel.MaxFileSizeBytes + 1024 * 1024;
});

var app = builder.Build();

// Apply migrations automatically
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
    db.Database.Migrate();
}

app.UseForwardedHeaders();

// Security headers go first so they also cover static files, error pages and redirects.
app.Use(async (context, next) =>
{
    var headers = context.Response.Headers;
    headers["Referrer-Policy"] = "no-referrer";
    headers["X-Content-Type-Options"] = "nosniff";
    headers["X-Frame-Options"] = "DENY";
    headers["Cross-Origin-Opener-Policy"] = "same-origin";
    headers["Permissions-Policy"] = "accelerometer=(), camera=(), geolocation=(), gyroscope=(), magnetometer=(), microphone=(), payment=(), usb=(), interest-cohort=(), browsing-topics=()";
    // Only same-origin scripts, no third-party connections. Uploaded files override this
    // with a stricter sandboxed policy in FileController.
    headers["Content-Security-Policy"] =
        "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data: blob:; " +
        "connect-src 'self'; font-src 'self'; object-src 'none'; base-uri 'self'; form-action 'self'; frame-ancestors 'none'";
    await next();
});

// Configure the HTTP request pipeline
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseStatusCodePagesWithReExecute("/not-found", "?statusCode={0}");
app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();
app.UseCors();
app.UseAntiforgery();

app.MapBlazorHub();
app.MapControllers();
app.MapFallbackToPage("/_Host");

app.Run();
