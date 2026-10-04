using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Wasla.Domain.Entities.Customer;
using Wasla.Infrastructure.Persistence.Tenant;

namespace Wasla.UnitTests.Persistence;

public sealed class PasswordResetTokenModelTests
{
    [Fact]
    public void Model_ConfiguresExpectedIndexesAndForeignKey()
    {
        var options = new DbContextOptionsBuilder<TenantDbContext>()
            .UseSqlite("Data Source=:memory:")
            .Options;
        using var db = new TenantDbContext(options);

        var entity = db.Model.FindEntityType(typeof(PasswordResetToken));
        Assert.NotNull(entity);
        Assert.Equal("PasswordResetTokens", entity!.GetTableName());

        var tokenHash = entity.FindProperty(nameof(PasswordResetToken.TokenHash));
        Assert.NotNull(tokenHash);
        Assert.False(tokenHash!.IsNullable);
        Assert.Equal(88, tokenHash.GetMaxLength());

        var tokenHashIndex = Assert.Single(entity.GetIndexes(), i =>
            i.GetDatabaseName() == "IX_PasswordResetTokens_TokenHash");
        Assert.True(tokenHashIndex.IsUnique);
        Assert.Equal(nameof(PasswordResetToken.TokenHash), Assert.Single(tokenHashIndex.Properties).Name);

        var userIdIndex = Assert.Single(entity.GetIndexes(), i =>
            i.GetDatabaseName() == "IX_PasswordResetTokens_UserId");
        Assert.False(userIdIndex.IsUnique);
        Assert.Equal(nameof(PasswordResetToken.UserId), Assert.Single(userIdIndex.Properties).Name);

        var expiresIndex = Assert.Single(entity.GetIndexes(), i =>
            i.GetDatabaseName() == "IX_PasswordResetTokens_ExpiresAtUtc");
        Assert.False(expiresIndex.IsUnique);
        Assert.Equal(nameof(PasswordResetToken.ExpiresAtUtc), Assert.Single(expiresIndex.Properties).Name);

        var foreignKey = Assert.Single(entity.GetForeignKeys());
        Assert.Equal(DeleteBehavior.Cascade, foreignKey.DeleteBehavior);
        Assert.True(foreignKey.IsRequired);
        Assert.Equal(typeof(AppUser), foreignKey.PrincipalEntityType.ClrType);
        Assert.Equal(nameof(PasswordResetToken.UserId), Assert.Single(foreignKey.Properties).Name);
    }
}
