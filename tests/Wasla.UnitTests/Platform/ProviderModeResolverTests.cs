using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Primitives;
using Wasla.Infrastructure.Platform;

namespace Wasla.UnitTests.Platform;

public sealed class ProviderModeResolverTests
{
    [Fact]
    public void Resolve_AcceptsValidMockAndRealValues()
    {
        Assert.Equal(
            PlatformProviderMode.Mock,
            ProviderModeResolver.Resolve(new DictionaryConfiguration("Platforms:ProviderMode", " Mock ")).Mode);

        Assert.Equal(
            PlatformProviderMode.Real,
            ProviderModeResolver.Resolve(new DictionaryConfiguration("Platforms:ProviderMode", "real")).Mode);
    }

    [Fact]
    public void Resolve_RejectsMissingOrInvalidValues()
    {
        Assert.Throws<InvalidOperationException>(
            () => ProviderModeResolver.Resolve(new DictionaryConfiguration()));

        Assert.Throws<InvalidOperationException>(
            () => ProviderModeResolver.Resolve(new DictionaryConfiguration("Platforms:ProviderMode", "Legacy")));
    }

    private sealed class DictionaryConfiguration : IConfiguration
    {
        private readonly Dictionary<string, string?> _values = new(StringComparer.OrdinalIgnoreCase);

        public DictionaryConfiguration()
        {
        }

        public DictionaryConfiguration(string key, string? value)
        {
            _values[key] = value;
        }

        public string? this[string key]
        {
            get => _values.TryGetValue(key, out var value) ? value : null;
            set => _values[key] = value;
        }

        public IEnumerable<IConfigurationSection> GetChildren() => [];

        public IChangeToken GetReloadToken() => TestChangeToken.Singleton;

        public IConfigurationSection GetSection(string key) => new DictionaryConfigurationSection(key, this[key]);
    }

    private sealed class DictionaryConfigurationSection(string key, string? value) : IConfigurationSection
    {
        public string Key { get; } = key;

        public string Path => Key;

        public string? Value { get; set; } = value;

        public string? this[string key]
        {
            get => null;
            set { }
        }

        public IEnumerable<IConfigurationSection> GetChildren() => [];

        public IChangeToken GetReloadToken() => TestChangeToken.Singleton;

        public IConfigurationSection GetSection(string key) => new DictionaryConfigurationSection(key, null);
    }

    private sealed class TestChangeToken : IChangeToken
    {
        public static readonly TestChangeToken Singleton = new();

        public bool ActiveChangeCallbacks => false;

        public bool HasChanged => false;

        public IDisposable RegisterChangeCallback(Action<object?> callback, object? state) =>
            TestDisposable.Instance;
    }

    private sealed class TestDisposable : IDisposable
    {
        public static readonly TestDisposable Instance = new();

        public void Dispose()
        {
        }
    }
}
