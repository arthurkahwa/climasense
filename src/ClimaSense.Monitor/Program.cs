using ClimaSense.Monitor.Data;
using ClimaSense.Monitor.Domain;
using ClimaSense.Monitor.Endpoints;
using ClimaSense.Monitor.Services;
using Microsoft.Extensions.Caching.Memory;

var de = System.Globalization.CultureInfo.GetCultureInfo("de-DE");
System.Globalization.CultureInfo.DefaultThreadCurrentCulture = de;
System.Globalization.CultureInfo.DefaultThreadCurrentUICulture = de;

// One-time production secret provisioning (run on the IIS server, as admin):
//   ClimaSense.Monitor.exe set-secret  -> prompts for the connection string and DPAPI-encrypts it
//   to %ProgramData%\ClimaSense\ups3.secret. The app reads + decrypts it at startup (Windows only).
if (args.Length > 0 && args[0] is "set-secret" or "--set-secret")
{
    Console.Write("Paste the ups3 connection string, then press Enter:\n> ");
    var conn = Console.ReadLine();
    if (string.IsNullOrWhiteSpace(conn)) { Console.Error.WriteLine("No input — nothing written."); return 1; }
    ProtectedSecret.Write(ProtectedSecret.DefaultPath, conn.Trim());
    Console.WriteLine($"Encrypted secret written to {ProtectedSecret.DefaultPath}");
    return 0;
}

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<EnvelopeOptions>(builder.Configuration.GetSection(EnvelopeOptions.SectionName));
builder.Services.AddSingleton<IClock, SystemClock>();
builder.Services.AddMemoryCache();

var connectionString = builder.Configuration.GetConnectionString("Ups3")
    ?? Environment.GetEnvironmentVariable("CLIMASENSE_UPS3_CONNECTION")
    ?? ProtectedSecret.Read(ProtectedSecret.DefaultPath)
    ?? throw new InvalidOperationException(
        "No ups3 connection string. Dev: user-secret ConnectionStrings:Ups3 or env CLIMASENSE_UPS3_CONNECTION. "
        + "Prod: run 'ClimaSense.Monitor.exe set-secret' to create the DPAPI-protected secret.");

builder.Services.AddSingleton<ISensorReadingRepository>(sp =>
    new CachingSensorReadingRepository(
        new SqlSensorReadingRepository(connectionString),
        sp.GetRequiredService<IMemoryCache>()));
builder.Services.AddScoped<ReadingsService>();
builder.Services.AddScoped<InsightsService>();

builder.Services.AddRazorPages();
builder.Services.AddHealthChecks().AddCheck<DbFeedHealthCheck>("feed");
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<DbExceptionHandler>();
builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));

var app = builder.Build();

app.UseExceptionHandler();
if (!app.Environment.IsDevelopment()) app.UseHsts();
app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();
app.MapRazorPages();
app.MapReadingsApi();
app.MapHealthChecks("/health");

app.Run();
return 0;

public partial class Program { }
