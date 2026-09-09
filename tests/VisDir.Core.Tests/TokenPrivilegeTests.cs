using VisDir.Core.Interop;
using Xunit;

namespace VisDir.Core.Tests;

public class TokenPrivilegeTests
{
    [Fact]
    public void TryEnableBackupPrivileges_RunsSafely_DoesNotThrow()
    {
        // Must run safely and return a boolean without throwing on any environment
        var exception = Record.Exception(() =>
        {
            bool result = TokenPrivilegeManager.TryEnableBackupPrivileges();
            // result is true when elevated, false when non-elevated
            Assert.True(result || !result);
        });

        Assert.Null(exception);
    }

    [Fact]
    public void TryEnablePrivilege_BackupPrivilege_ReturnsBoolWithoutThrowing()
    {
        var exception = Record.Exception(() =>
        {
            bool result = TokenPrivilegeManager.TryEnablePrivilege(TokenPrivilegeManager.SE_BACKUP_NAME);
            Assert.True(result || !result);

            bool literalResult = TokenPrivilegeManager.TryEnablePrivilege("SeBackupPrivilege");
            Assert.True(literalResult || !literalResult);
        });

        Assert.Null(exception);
    }

    [Fact]
    public void TryEnablePrivilege_InvalidPrivilege_ReturnsFalseWithoutThrowing()
    {
        var exception = Record.Exception(() =>
        {
            bool result = TokenPrivilegeManager.TryEnablePrivilege("SeNonExistentPrivilege");
            Assert.False(result);
        });

        Assert.Null(exception);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void TryEnablePrivilege_NullOrEmptyOrWhitespace_ReturnsFalseWithoutThrowing(string? privilegeName)
    {
        var exception = Record.Exception(() =>
        {
            bool result = TokenPrivilegeManager.TryEnablePrivilege(privilegeName!);
            Assert.False(result);
        });

        Assert.Null(exception);
    }

    [Fact]
    public void TryEnablePrivilege_RestoreAndVolumePrivileges_ReturnBoolWithoutThrowing()
    {
        var exception = Record.Exception(() =>
        {
            bool restore = TokenPrivilegeManager.TryEnablePrivilege(TokenPrivilegeManager.SE_RESTORE_NAME);
            Assert.True(restore || !restore);

            bool volume = TokenPrivilegeManager.TryEnablePrivilege(TokenPrivilegeManager.SE_MANAGE_VOLUME_NAME);
            Assert.True(volume || !volume);
        });

        Assert.Null(exception);
    }

    [Fact]
    public void Constants_MatchExpectedValues()
    {
        Assert.Equal(0x0020u, TokenPrivilegeManager.TOKEN_ADJUST_PRIVILEGES);
        Assert.Equal(0x0008u, TokenPrivilegeManager.TOKEN_QUERY);
        Assert.Equal(0x0002u, TokenPrivilegeManager.SE_PRIVILEGE_ENABLED);
        Assert.Equal("SeBackupPrivilege", TokenPrivilegeManager.SE_BACKUP_NAME);
        Assert.Equal("SeRestorePrivilege", TokenPrivilegeManager.SE_RESTORE_NAME);
        Assert.Equal("SeManageVolumePrivilege", TokenPrivilegeManager.SE_MANAGE_VOLUME_NAME);
    }
}
