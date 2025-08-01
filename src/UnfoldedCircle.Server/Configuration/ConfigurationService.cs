using UnfoldedCircle.Server.Json;

namespace UnfoldedCircle.Server.Configuration;

internal sealed class ConfigurationService(IConfiguration configuration, UnfoldedCircleJsonSerializerContext jsonSerializerContext,
    ILogger<ConfigurationService> logger)
    : IConfigurationService
{
    private readonly IConfiguration _configuration = configuration;
    private readonly UnfoldedCircleJsonSerializerContext _jsonSerializerContext = jsonSerializerContext;
    private readonly ILogger<ConfigurationService> _logger = logger;
    private string? _ucConfigHome;
    private string UcConfigHome => _ucConfigHome ??= _configuration["UC_CONFIG_HOME"] ?? string.Empty;
    private string ConfigurationFilePath => Path.Combine(UcConfigHome, "configured_entities.json");
    private UnfoldedCircleConfiguration? _unfoldedCircleConfiguration;
    private readonly SemaphoreSlim _unfoldedCircleConfigSemaphore = new(1, 1);

    public async Task<UnfoldedCircleConfiguration> GetConfigurationAsync(CancellationToken cancellationToken = default)
    {
        if (_unfoldedCircleConfiguration is not null)
            return _unfoldedCircleConfiguration;

        await _unfoldedCircleConfigSemaphore.WaitAsync(cancellationToken);

        try
        {
            if (_unfoldedCircleConfiguration is not null)
                return _unfoldedCircleConfiguration;

            var configurationFilePath = ConfigurationFilePath;
            if (File.Exists(configurationFilePath))
            {
                try
                {
                    await using var configurationFile = File.Open(configurationFilePath, FileMode.Open);
                    var deserialized = await JsonSerializer.DeserializeAsync(configurationFile,
                        _jsonSerializerContext.UnfoldedCircleConfiguration,
                        cancellationToken);
                    _unfoldedCircleConfiguration = deserialized ?? throw new InvalidOperationException("Failed to deserialize configuration");
                    return _unfoldedCircleConfiguration;
                }
                catch (Exception e) when (e.GetType().FullName?.Equals("System.Text.Json.JsonReaderException", StringComparison.Ordinal) is true)
                {
                    _logger.LogError(e, "Configuration file '{ConfigurationFilePath}' is corrupted, creating a new configuration",
                        configurationFilePath);
                    return await CreateNewConfiguration(configurationFilePath, cancellationToken);
                }
            }

            return await CreateNewConfiguration(configurationFilePath, cancellationToken);
        }
        finally
        {
            _unfoldedCircleConfigSemaphore.Release();
        }
    }

    public async Task<UnfoldedCircleConfigurationItem?> GetConfigurationItemAsync(string ipaddress, CancellationToken cancellationToken = default)
    {
        var unfoldedCircleConfiguration = await GetConfigurationAsync(cancellationToken);
        return unfoldedCircleConfiguration.Entities.FirstOrDefault(x => x.IpAddress.Equals(ipaddress, StringComparison.OrdinalIgnoreCase));
    }

    private async Task<UnfoldedCircleConfiguration> CreateNewConfiguration(string configurationFilePath, CancellationToken cancellationToken)
    {
        _unfoldedCircleConfiguration = new UnfoldedCircleConfiguration
        {
            Entities = []
        };
        await using var configurationFile = File.Create(configurationFilePath);
        await JsonSerializer.SerializeAsync(configurationFile,
            _unfoldedCircleConfiguration,
            _jsonSerializerContext.UnfoldedCircleConfiguration,
            cancellationToken);

        return _unfoldedCircleConfiguration;
    }

    public async Task<UnfoldedCircleConfiguration> UpdateConfigurationAsync(UnfoldedCircleConfiguration configuration, CancellationToken cancellationToken = default)
    {
        await _unfoldedCircleConfigSemaphore.WaitAsync(cancellationToken);
        
        try
        {
            await using var configurationFileStream = File.Create(ConfigurationFilePath);
            await JsonSerializer.SerializeAsync(configurationFileStream, configuration, _jsonSerializerContext.UnfoldedCircleConfiguration, cancellationToken);
            _unfoldedCircleConfiguration = configuration;
            return _unfoldedCircleConfiguration;
        }
        finally
        {
            _unfoldedCircleConfigSemaphore.Release();
        }
    }
}