using CopilotPluginApi.Configuration;
using CopilotPluginApi.Data;
using CopilotPluginApi.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Microsoft.ML.Tokenizers;
using StackExchange.Redis;

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

var databaseProvider = DatabaseProviderResolver.Resolve(
    Environment.GetEnvironmentVariable(DatabaseEnvironmentVariableNames.Provider));
var databaseConnectionString = Environment.GetEnvironmentVariable(DatabaseEnvironmentVariableNames.ConnectionString)
    ?? throw new InvalidOperationException(
        $"The {DatabaseEnvironmentVariableNames.ConnectionString} environment variable must be set.");

// Config
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

// Redis
builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);
builder.Services.AddSingleton<IConnectionMultiplexer>(serviceProvider =>
{
    var redisConfiguration = serviceProvider.GetRequiredService<IOptions<RedisConfig>>().Value;
    var redisOptions = ConfigurationOptions.Parse(redisConfiguration.ConnectionString);
    redisOptions.AbortOnConnectFail = false;

    return ConnectionMultiplexer.Connect(redisOptions);
});

// Database
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

builder.Services.AddScoped<IAuditLogger, AuditLogger>();

// Services
builder.Services.AddSingleton<Tokenizer>(_ => TiktokenTokenizer.CreateForModel(TokenizerModelNames.Gpt4o));
builder.Services.AddSingleton<IRateLimiterService, RateLimiterService>();
builder.Services.AddSingleton<IIdempotencyService, IdempotencyService>();
builder.Services.AddScoped<IMemoryService, MemoryService>();
builder.Services.AddScoped<ISemanticCacheService, SemanticCacheService>();
builder.Services.AddSingleton<IPromptBuilderService, PromptBuilderService>();

// Controllers
builder.Services.AddControllers();

var app = builder.Build();

app.MapControllers();

app.Run();

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
            var value when value.Equals(SqliteProviderName, StringComparison.OrdinalIgnoreCase) => DatabaseProvider.Sqlite,
            var value when value.Equals(PostgreSqlProviderName, StringComparison.OrdinalIgnoreCase) => DatabaseProvider.PostgreSql,
            var value when value.Equals(PostgresProviderAlias, StringComparison.OrdinalIgnoreCase) => DatabaseProvider.PostgreSql,
            _ => throw new InvalidOperationException(
                $"Unsupported value '{providerName}' for {DatabaseEnvironmentVariableNames.Provider}. Supported values are '{SqliteProviderName}' and '{PostgreSqlProviderName}'.")
        };
}

internal static class TokenizerModelNames
{
    internal const string Gpt4o = "gpt-4o";
}
