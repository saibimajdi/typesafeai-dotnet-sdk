using System.Globalization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace TypeSafeAI.DependencyInjection;

/// <summary>
/// Dependency injection registration for the TypeSafe SDK.
/// </summary>
/// <remarks>
/// <para>
/// The client is registered on top of <see cref="IHttpClientFactory"/>, so its handler is pooled
/// and rotated by the framework and the client itself can be resolved as a singleton without
/// risking socket exhaustion.
/// </para>
/// <para>
/// The SDK's own retry policy is left in charge. A resilience handler is deliberately not attached,
/// because retrying at two layers multiplies attempts and makes the documented backoff and budget
/// semantics impossible to reason about.
/// </para>
/// </remarks>
public static class TypeSafeServiceCollectionExtensions
{
    /// <summary>
    /// The name of the <see cref="IHttpClientFactory"/> client the SDK resolves.
    /// </summary>
    public const string HttpClientName = "TypeSafe";

    /// <summary>
    /// The configuration section bound to <see cref="TypeSafeClientOptions"/>.
    /// </summary>
    public const string ConfigurationSectionName = "TypeSafe";

    /// <summary>
    /// Registers a <see cref="ITypeSafeClient"/> backed by <see cref="IHttpClientFactory"/>.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">An optional callback applied after configuration binding.</param>
    /// <returns>The <see cref="IHttpClientBuilder"/>, so a handler can be added.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// Options are resolved from the <c>TYPESAFE_*</c> environment variables and then from the SDK
    /// defaults, with <paramref name="configure"/> applied last.
    /// </remarks>
    public static IHttpClientBuilder AddTypeSafeClient(
        this IServiceCollection services,
        Action<TypeSafeClientOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        return services.AddTypeSafeClientCore(
            services.AddOptions<TypeSafeClientOptions>().Configure(options => configure?.Invoke(options)));
    }

    /// <summary>
    /// Registers a <see cref="ITypeSafeClient"/> backed by <see cref="IHttpClientFactory"/>, binding
    /// settings from configuration.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">The configuration to bind the <c>TypeSafe</c> section from.</param>
    /// <param name="configure">An optional callback applied after configuration binding.</param>
    /// <returns>The <see cref="IHttpClientBuilder"/>, so a handler can be added.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="services"/> or <paramref name="configuration"/> is <see langword="null"/>.
    /// </exception>
    /// <remarks>
    /// Because the default configuration providers include environment variables, binding this
    /// section is what lets <c>TYPESAFE_API_KEY</c> and friends flow in unchanged.
    /// </remarks>
    public static IHttpClientBuilder AddTypeSafeClient(
        this IServiceCollection services,
        IConfiguration configuration,
        Action<TypeSafeClientOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(ConfigurationSectionName);

        return services.AddTypeSafeClientCore(
            services.AddOptions<TypeSafeClientOptions>()
                .Configure(options => BindSection(section, options))
                .Configure(options => configure?.Invoke(options)));
    }

    /// <summary>
    /// Copies the <c>TypeSafe</c> configuration section onto an options instance.
    /// </summary>
    /// <param name="section">The configuration section.</param>
    /// <param name="options">The options to populate.</param>
    /// <remarks>
    /// Binding is done by hand rather than with <c>ConfigurationBinder.Bind</c>, which is annotated
    /// as requiring unreferenced and dynamic code. Doing it explicitly keeps this package usable
    /// under trimming and ahead-of-time compilation, and makes the accepted key names and formats
    /// visible in one place. Values that are absent or unparseable are left untouched, so the
    /// environment variables and SDK defaults still apply underneath.
    /// </remarks>
    private static void BindSection(IConfigurationSection section, TypeSafeClientOptions options)
    {
        if (Read(section, nameof(TypeSafeClientOptions.ApiKey)) is { Length: > 0 } apiKey)
        {
            options.ApiKey = apiKey;
        }

        if (Read(section, nameof(TypeSafeClientOptions.Model)) is { Length: > 0 } model)
        {
            options.Model = model;
        }

        if (Read(section, nameof(TypeSafeClientOptions.BaseUrl)) is { Length: > 0 } baseUrl &&
            Uri.TryCreate(baseUrl, UriKind.Absolute, out var parsedBaseUrl))
        {
            options.BaseUrl = parsedBaseUrl;
        }

        if (Read(section, nameof(TypeSafeClientOptions.UserAgent)) is { Length: > 0 } userAgent)
        {
            options.UserAgent = userAgent;
        }

        if (Read(section, nameof(TypeSafeClientOptions.Timeout)) is { Length: > 0 } timeout &&
            TimeSpan.TryParse(timeout, CultureInfo.InvariantCulture, out var parsedTimeout))
        {
            options.Timeout = parsedTimeout;
        }

        if (Read(section, nameof(TypeSafeClientOptions.UseEnvironmentFallback)) is { Length: > 0 } fallback &&
            bool.TryParse(fallback, out var parsedFallback))
        {
            options.UseEnvironmentFallback = parsedFallback;
        }

        if (section.GetSection(nameof(TypeSafeClientOptions.DefaultHeaders)).GetChildren().Any())
        {
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var header in section.GetSection(nameof(TypeSafeClientOptions.DefaultHeaders)).GetChildren())
            {
                if (header.Value is { Length: > 0 } value)
                {
                    headers[header.Key] = value;
                }
            }

            options.DefaultHeaders = headers;
        }
    }

    private static string? Read(IConfigurationSection section, string key)
    {
        // The documented environment variable name is accepted as an alias for each key, so a
        // single flat set of TYPESAFE_* variables configures the client whichever registration
        // overload is used.
        var environmentName = key switch
        {
            nameof(TypeSafeClientOptions.ApiKey) => TypeSafeDefaults.ApiKeyEnvironmentVariable,
            nameof(TypeSafeClientOptions.Model) => TypeSafeDefaults.DefaultModelEnvironmentVariable,
            nameof(TypeSafeClientOptions.BaseUrl) => TypeSafeDefaults.BaseUrlEnvironmentVariable,
            _ => null,
        };

        var value = section[key];

        if (!string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        return environmentName is null ? null : section[environmentName];
    }

    private static IHttpClientBuilder AddTypeSafeClientCore(
        this IServiceCollection services,
        OptionsBuilder<TypeSafeClientOptions> optionsBuilder)
    {
        // Resolve the logger factory from the container unless the caller supplied one.
        optionsBuilder.Configure<IServiceProvider>(static (options, provider) =>
            options.LoggerFactory ??= provider.GetService<ILoggerFactory>());

        optionsBuilder
            .Validate(
                static options => options.Timeout > TimeSpan.Zero || options.Timeout == Timeout.InfiniteTimeSpan,
                "TypeSafe Timeout must be positive, or Timeout.InfiniteTimeSpan to disable it.")
            .Validate(
                static options => options.BaseUrl is null || options.BaseUrl.IsAbsoluteUri,
                "TypeSafe BaseUrl must be an absolute URI.")
            .ValidateOnStart();

        var builder = services.AddHttpClient(HttpClientName);

        // Every request carries the API key, and IHttpClientFactory's default handler logs all
        // request headers at Trace level. On Microsoft.Extensions.Http 8.x that log line contains
        // the Authorization header verbatim, so a consumer who turns on Trace logging would write
        // live credentials to their logs. Redacting here is defence in depth: the SDK's own debug
        // logging already masks credential-shaped headers, but the framework's does not.
        builder.RedactLoggedHeaders(static _ => true);

        services.AddSingleton(static provider => (TypeSafeClient)CreateClient(provider));
        services.AddSingleton<ITypeSafeClient>(static provider => provider.GetRequiredService<TypeSafeClient>());

        return builder;
    }

    private static TypeSafeClient CreateClient(IServiceProvider provider)
    {
        var options = provider.GetRequiredService<IOptions<TypeSafeClientOptions>>().Value;
        var factory = provider.GetRequiredService<IHttpClientFactory>();
        var http = factory.CreateClient(HttpClientName);

        // The client is owned by the factory, so TypeSafeClient must not dispose it. No BaseAddress
        // is set: TypeSafeClient combines the base URL itself, which keeps the standalone and
        // container paths building identical request URIs.
        return new TypeSafeClient(options, http);
    }
}
