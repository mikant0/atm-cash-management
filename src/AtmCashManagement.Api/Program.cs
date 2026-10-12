using System.Reflection;
using AtmCashManagement.Api.Application;
using AtmCashManagement.Api.BackgroundServices;
using AtmCashManagement.Api.Infrastructure;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi.Models;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<CashOptions>(builder.Configuration.GetSection(CashOptions.SectionName));

// The connection string is read when the context is first needed, not here, so that hosts which
// replace configuration late (integration tests) are honoured.
builder.Services.AddDbContext<AppDbContext>((services, options) => options
    .UseNpgsql(services.GetRequiredService<IConfiguration>().GetConnectionString("Default"))
    .UseSnakeCaseNamingConvention());

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<CardHasher>();
builder.Services.AddSingleton<BusinessCalendar>();
builder.Services.AddScoped<CassetteLedger>();
builder.Services.AddScoped<AtmService>();
builder.Services.AddScoped<WithdrawalService>();
builder.Services.AddScoped<DepositService>();
builder.Services.AddScoped<ReconciliationService>();
builder.Services.AddScoped<ReportService>();

builder.Services.AddHostedService<QuoteExpiryService>();
builder.Services.AddHostedService<OutboxDispatcher>();

builder.Services
    .AddControllers()
    .AddJsonOptions(options => JsonDefaults.Configure(options.JsonSerializerOptions))
    .ConfigureApiBehaviorOptions(options => options.InvalidModelStateResponseFactory = Problems.InvalidModelState);

builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
builder.Services.AddProblemDetails(options => options.CustomizeProblemDetails = context =>
{
    // Errors produced by the framework itself (unknown route, wrong method, ...).
    context.ProblemDetails.Extensions.TryAdd("code", $"http.{context.ProblemDetails.Status}");
    Problems.AddTraceId(context.HttpContext, context.ProblemDetails);
});

builder.Services.AddHealthChecks().AddDbContextCheck<AppDbContext>("database", tags: ["ready"]);

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "ATM Cash Management API",
        Version = "v1",
        Description = "Cash management backend for a bank's ATM network. " +
                      "All errors are returned as ProblemDetails with a machine-readable 'code'."
    });
    options.IncludeXmlComments(Path.Combine(AppContext.BaseDirectory,
        $"{Assembly.GetExecutingAssembly().GetName().Name}.xml"));
    options.SupportNonNullableReferenceTypes();
});

var app = builder.Build();

if (app.Configuration.GetValue("Database:MigrateOnStartup", true))
{
    await app.MigrateDatabaseAsync();
}

app.UseExceptionHandler();
app.UseStatusCodePages();

app.UseSwagger();
app.UseSwaggerUI(options => options.DocumentTitle = "ATM Cash Management API");

app.MapControllers();

// /health: the process is up. /health/ready: it can also reach its database.
app.MapHealthChecks("/health", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains("ready") });
app.MapGet("/", () => Results.Redirect("/swagger")).ExcludeFromDescription();

app.Run();

public partial class Program;
