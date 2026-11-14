using Wasla.UnitTests.Admin;

namespace Wasla.UnitTests.Persistence;

/// <summary>
/// WAS-206: a serializable transaction is begun only through <c>SerializableTransaction</c>, which hands its pooled SQL
/// Server session back at read committed. The behaviour itself is tested on SQL Server in
/// <see cref="SerializableTransactionSqlServerTests"/>; this keeps a new serializable transaction from bypassing it.
/// </summary>
public sealed class SerializableTransactionContractTests
{
    private const string Helper = "src/Wasla.Infrastructure/Persistence/SerializableTransaction.cs";

    [Fact]
    public void OnlyTheHelper_AsksForSerializableIsolation()
    {
        var src = TenantOperationsRulesTests.RepoFile("src");
        var root = Path.GetDirectoryName(src)!;
        var offenders = Directory
            .EnumerateFiles(src, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Split(Path.DirectorySeparatorChar).Any(part => part is "bin" or "obj"))
            .Where(path => File.ReadAllText(path).Contains("IsolationLevel.Serializable", StringComparison.Ordinal))
            .Select(path => Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/'))
            .Where(path => path != Helper)
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.True(File.Exists(Path.Combine(root, Helper)), $"{Helper} is missing.");
        Assert.Empty(offenders);
    }
}
