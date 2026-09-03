using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SW.CloudFiles.Extensions;
using SW.PrimitiveTypes;
using SW.Serverless;
using SW.Serverless.Resident;
using SW.Serverless.SampleWeb.Components;
using SW.Serverless.SampleWeb.Services;
using SW.Serverless.SampleWeb.Telemetry;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

var builder = WebApplication.CreateBuilder(args);

// ---------------------------------------------------------------- storage and serverless

// Local filesystem ICloudFilesService — the real install-from-storage path, no credentials.
builder.Services.AddLocalTestsCloudFiles(o => o.BucketName = "sw-serverless-sample");

builder.Services.AddServerless(o =>
{
    o.AdapterRemotePath = "adapters";
    o.AdapterLocalPath = Path.Combine(Path.GetTempPath(), "swsl-sampleweb", "installed");
    o.AdapterMetadataCacheDuration = 1;
    o.CommandTimeout = 30;
});

builder.Services.AddResidentAdapters<DashboardEventSink>(o =>
{
    o.HeartbeatInterval = TimeSpan.FromSeconds(5);
    o.MaxInFlight = 8;
    o.SoftMemoryLimitBytes = 512L * 1024 * 1024;
    o.CrashLoopThreshold = 4;
});

// ---------------------------------------------------------------- observability

builder.Services.AddSingleton<DashboardState>();
builder.Services.AddHostedService<AdapterMetricListener>();
builder.Services.AddSingleton<AdapterPackager>();
builder.Services.AddSingleton<DemoBootstrapper>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<DemoBootstrapper>());

builder.Logging.Services.AddSingleton<Microsoft.Extensions.Logging.ILoggerProvider>(
    sp => new AdapterLogCaptureProvider(sp.GetRequiredService<DashboardState>()));

builder.Services.AddRazorComponents().AddInteractiveServerComponents();

var app = builder.Build();

if (!app.Environment.IsDevelopment()) app.UseExceptionHandler("/Error", createScopeForErrors: true);
app.UseStaticFiles();
app.UseAntiforgery();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

// ---------------------------------------------------------------- classic lifecycle API
// Kept as plain endpoints to contrast with the dashboard: one process per call, started and
// disposed inside the request scope, exactly as before protocol 2.

app.MapGet("/api/classic/{adapterId}/expected", async (string adapterId, IServerlessService serverless) =>
{
    await serverless.StartAsync(adapterId, Guid.NewGuid().ToString("N"));
    return Results.Ok(await serverless.GetExpectedStartupValues());
});

app.MapPost("/api/classic/{adapterId}/{command}", async (string adapterId, string command,
    HttpRequest request, IServerlessService serverless) =>
{
    using var reader = new StreamReader(request.Body);
    var body = await reader.ReadToEndAsync();

    await serverless.StartAsync(adapterId, Guid.NewGuid().ToString("N"),
        new Dictionary<string, string> { ["UserName"] = "sample", ["Password"] = "sample" });

    var result = await serverless.InvokeAsync<string>(command, string.IsNullOrWhiteSpace(body) ? null : body);
    return Results.Ok(new { adapterId, command, result });
});

// ---------------------------------------------------------------- resident lifecycle API

app.MapGet("/api/adapters", (IResidentAdapterHost adapters) => Results.Ok(adapters.Describe()));

app.MapPost("/api/adapters/{adapterId}/{instanceKey}/{command}", async (string adapterId,
    string instanceKey, string command, HttpRequest request, IResidentAdapterHost adapters) =>
{
    var instance = adapters.Get(adapterId, instanceKey);
    if (instance == null) return Results.NotFound();

    using var reader = new StreamReader(request.Body);
    var body = await reader.ReadToEndAsync();

    try
    {
        return Results.Ok(new
        {
            result = await instance.InvokeAsync<string>(command, string.IsNullOrWhiteSpace(body) ? null : body)
        });
    }
    catch (AdapterInvocationException ex)
    {
        return Results.Problem(title: ex.AdapterExceptionType, detail: ex.Message, statusCode: 502);
    }
});

app.Run();
