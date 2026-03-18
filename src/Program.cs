using System.ClientModel;
using System.ClientModel.Primitives;
using System.Text.Json;
using System.Text.Json.Serialization;
using Azure.AI.OpenAI;
using CopilotPluginApi.Configuration;
using CopilotPluginApi.Data;
using CopilotPluginApi.Services;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Microsoft.ML.Tokenizers;
using Microsoft.OpenApi.Models;
using StackExchange.Redis;

DotNetEnv.Env.Load();

// ── 1. Builder ──────────────────────────────────────────────────
var builder = WebApplication.CreateBuilder(args);

var repositoryRoot = Path.GetFullPath(Path.Combine(builder.Environment.ContentRootPath, ".."));
var localAppSettingsPath = Path.Combine(builder.Environment.ContentRootPath, ApplicationConfigurationFiles.Default);
var repositoryAppSettingsPath = Path.Combine(repositoryRoot, ApplicationConfigurationFiles.Default);

if (!File.Exists(localAppSettingsPath) && File.Exists(repositoryAppSettingsPath))
{
    builder.Configuration
        .SetBasePath(repositoryRoot)
        .AddJsonFile(ApplicationConfigurationFiles.Default, optional: false, reloadOnChange: true)
        .AddJsonFile(
            ApplicationConfigurationFiles.GetEnvironmentSpecific(builder.Environment.EnvironmentName),
            optional: true,
            reloadOnChange: true);
}

builder.Configuration.AddEnvironmentVariables();

// ── 2. Configuration ────────────────────────────────────────────
builder.Services
    .AddOptions<AzureOpenAIConfig>()
    .BindConfiguration(AzureOpenAIConfig.SectionName)
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services
    .AddOptions<RedisConfig>()
    .BindConfiguration(RedisConfig.SectionName)
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services
    .AddOptions<PromptConfig>()
    .BindConfiguration(PromptConfig.SectionName)
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services
    .AddOptions<MemoryConfig>()
    .BindConfiguration(MemoryConfig.SectionName)
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services
    .AddOptions<SemanticCacheConfig>()
    .BindConfiguration(SemanticCacheConfig.SectionName)
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services
    .AddOptions<RateLimitConfig>()
    .BindConfiguration(RateLimitConfig.SectionName)
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services
    .AddOptions<CostsConfig>()
    .BindConfiguration(CostsConfig.SectionName)
    .ValidateDataAnnotations()
    .ValidateOnStart();

var databaseProvider = DatabaseProviderResolver.Resolve(
    Environment.GetEnvironmentVariable(DatabaseEnvironmentVariableNames.Provider));
var databaseConnectionString = Environment.GetEnvironmentVariable(DatabaseEnvironmentVariableNames.ConnectionString)
    ?? throw new InvalidOperationException(
        $"The {DatabaseEnvironmentVariableNames.ConnectionString} environment variable must be set.");

// ── 3. Redis ────────────────────────────────────────────────────
var redisConnectionString = builder.Configuration
    .GetSection(RedisConfig.SectionName)
    .Get<RedisConfig>()?.ConnectionString ?? RedisDefaults.FallbackConnectionString;

builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);
builder.Services.AddSingleton<IConnectionMultiplexer>(serviceProvider =>
{
    var config = serviceProvider.GetRequiredService<IOptions<RedisConfig>>().Value;
    var redisLogger = serviceProvider.GetRequiredService<ILogger<ConnectionMultiplexer>>();

    try
    {
        var options = ConfigurationOptions.Parse(config.ConnectionString);
        options.AbortOnConnectFail = false;

        var multiplexer = ConnectionMultiplexer.Connect(options);

        multiplexer.ConnectionFailed += (_, args) =>
            redisLogger.LogWarning(args.Exception, "Redis connection lost: {FailureType}.", args.FailureType);

        multiplexer.ConnectionRestored += (_, args) =>
            redisLogger.LogInformation("Redis connection restored: {EndPoint}.", args.EndPoint);

        if (!multiplexer.IsConnected)
        {
            redisLogger.LogWarning(
                "Redis is not connected at startup. Services will handle Redis unavailability individually.");
        }

        return multiplexer;
    }
    catch (Exception ex)
    {
        redisLogger.LogWarning(
            ex,
            "Failed to establish Redis connection at startup. Creating a disconnected multiplexer for graceful degradation.");

        var fallbackOptions = new ConfigurationOptions { AbortOnConnectFail = false };
        fallbackOptions.EndPoints.Add(config.ConnectionString);
        return ConnectionMultiplexer.Connect(fallbackOptions);
    }
});

// ── 4. Database ─────────────────────────────────────────────────
builder.Services.AddDbContext<AppDbContext>(options =>
{
    switch (databaseProvider)
    {
        case DatabaseProvider.Sqlite:
            options.UseSqlite(databaseConnectionString);
            break;
        case DatabaseProvider.PostgreSql:
            options.UseNpgsql(databaseConnectionString);
            break;
        default:
            throw new InvalidOperationException(
                $"Unsupported database provider '{databaseProvider}'.");
    }
});

// ── 5. Services ─────────────────────────────────────────────────
builder.Services.AddSingleton<AzureOpenAIClient>(serviceProvider =>
{
    var azureOpenAIConfiguration = serviceProvider.GetRequiredService<IOptions<AzureOpenAIConfig>>().Value;
    var clientOptions = new AzureOpenAIClientOptions
    {
        RetryPolicy = new ClientRetryPolicy(0)
    };

    return new AzureOpenAIClient(
        new Uri(azureOpenAIConfiguration.Endpoint),
        new ApiKeyCredential(azureOpenAIConfiguration.ApiKey),
        clientOptions);
});

builder.Services.AddSingleton<Tokenizer>(_ => TiktokenTokenizer.CreateForModel(TokenizerModelNames.Gpt4o));
builder.Services.AddSingleton<IRateLimiterService, RateLimiterService>();
builder.Services.AddSingleton<IIdempotencyService, IdempotencyService>();
builder.Services.AddSingleton<IPromptBuilderService, PromptBuilderService>();
builder.Services.AddScoped<IMemoryService, MemoryService>();
builder.Services.AddScoped<ISemanticCacheService, SemanticCacheService>();
builder.Services.AddScoped<ILlmOrchestratorService, LlmOrchestratorService>();
builder.Services.AddScoped<IAuditLogger, AuditLogger>();

// ── 6. Controllers & API ────────────────────────────────────────
builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        options.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
    });

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc(SwaggerConstants.DocumentVersion, new OpenApiInfo
    {
        Title = SwaggerConstants.ApiTitle,
        Version = SwaggerConstants.DocumentVersion
    });
});

// ── 7. Observability ────────────────────────────────────────────
builder.Logging.ClearProviders();
builder.Logging.SetMinimumLevel(LogLevel.Information);
builder.Logging.AddFilter(LogCategoryFilters.AspNetCore, LogLevel.Warning);
builder.Logging.AddFilter(LogCategoryFilters.EntityFrameworkCore, LogLevel.Warning);

if (builder.Environment.IsDevelopment())
{
    builder.Logging.AddSimpleConsole(options =>
    {
        options.IncludeScopes = true;
        options.TimestampFormat = "yyyy-MM-dd HH:mm:ss ";
    });
}
else
{
    builder.Logging.AddJsonConsole();
}

// ── 8. Health checks ────────────────────────────────────────────
builder.Services.AddHealthChecks()
    .AddRedis(redisConnectionString, name: HealthCheckNames.Redis)
    .AddDbContextCheck<AppDbContext>(name: HealthCheckNames.Database);

// ── Build ───────────────────────────────────────────────────────
var app = builder.Build();

// ── Auto-migrate database ───────────────────────────────────────
try
{
    using var migrationScope = app.Services.CreateScope();
    var dbContext = migrationScope.ServiceProvider.GetRequiredService<AppDbContext>();
    await dbContext.Database.MigrateAsync();
}
catch (Exception ex)
{
    app.Logger.LogError(
        ex,
        "Database migration failed on startup. The application will continue in a degraded state.");
}

// ── 9. Middleware pipeline ──────────────────────────────────────
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler(ErrorEndpoint.Path);
}

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();
app.UseRouting();
app.MapControllers();
app.MapHealthChecks(HealthCheckEndpoint.Path, new HealthCheckOptions
{
    ResponseWriter = HealthCheckResponseWriter.WriteAsync
});

// ── 10. Error endpoint ──────────────────────────────────────────
app.Map(ErrorEndpoint.Path, () => Results.Json(
    new { error = ErrorEndpoint.GenericMessage },
    statusCode: StatusCodes.Status500InternalServerError));

app.Run();

// ────────────────────────────────────────────────────────────────
// Supporting types used exclusively by the application entry point
// ────────────────────────────────────────────────────────────────

internal static class ApplicationConfigurationFiles
{
    internal const string Default = "appsettings.json";

    internal static string GetEnvironmentSpecific(string environmentName) =>
        $"appsettings.{environmentName}.json";
}

internal enum DatabaseProvider
{
    Sqlite,
    PostgreSql
}

internal static class DatabaseEnvironmentVariableNames
{
    internal const string Provider = "DATABASE_PROVIDER";
    internal const string ConnectionString = "DATABASE_CONNECTION_STRING";
}

internal static class DatabaseProviderResolver
{
    private const string SqliteProviderName = "sqlite";
    private const string PostgreSqlProviderName = "postgresql";
    private const string PostgresProviderAlias = "postgres";

    internal static DatabaseProvider Resolve(string? providerName) =>
        providerName switch
        {
            null or "" => DatabaseProvider.Sqlite,
            var value when value.Equals(SqliteProviderName, StringComparison.OrdinalIgnoreCase) =>
                DatabaseProvider.Sqlite,
            var value when value.Equals(PostgreSqlProviderName, StringComparison.OrdinalIgnoreCase) =>
                DatabaseProvider.PostgreSql,
            var value when value.Equals(PostgresProviderAlias, StringComparison.OrdinalIgnoreCase) =>
                DatabaseProvider.PostgreSql,
            _ => throw new InvalidOperationException(
                $"Unsupported value '{providerName}' for {DatabaseEnvironmentVariableNames.Provider}. " +
                $"Supported values are '{SqliteProviderName}' and '{PostgreSqlProviderName}'.")
        };
}

internal static class TokenizerModelNames
{
    internal const string Gpt4o = "gpt-4o";
}

internal static class RedisDefaults
{
    internal const string FallbackConnectionString = "localhost:6379";
}

internal static class SwaggerConstants
{
    internal const string ApiTitle = "Copilot Plugin API";
    internal const string DocumentVersion = "v1";
}

internal static class LogCategoryFilters
{
    internal const string AspNetCore = "Microsoft.AspNetCore";
    internal const string EntityFrameworkCore = "Microsoft.EntityFrameworkCore";
}

internal static class HealthCheckNames
{
    internal const string Redis = "redis";
    internal const string Database = "database";
}

internal static class HealthCheckEndpoint
{
    internal const string Path = "/health";
}

internal static class ErrorEndpoint
{
    internal const string Path = "/error";
    internal const string GenericMessage = "An unexpected error occurred";
}

internal static class HealthCheckResponseWriter
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    internal static async Task WriteAsync(HttpContext context, HealthReport report)
    {
        var dependencies = new Dictionary<string, string>(report.Entries.Count);
        foreach (var entry in report.Entries)
        {
            dependencies[entry.Key] = entry.Value.Status.ToString();
        }

        await context.Response.WriteAsJsonAsync(
            new { status = report.Status.ToString(), dependencies },
            SerializerOptions);
    }
}
