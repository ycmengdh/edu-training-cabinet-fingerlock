using Xunit;

namespace CabinetLock.Tests
{
    public class MessageHmacTests
    {
        [Fact]
        public void Sign_IsDeterministic_ForSameCanonicalInput()
        {
            string a = MessageHmac.Sign("secret", "CONTROL_LOCK", "CABINET_001",
                "msg1", 1700000000, "nonce01", "{\"lock_id\":1}");
            string b = MessageHmac.Sign("secret", "CONTROL_LOCK", "CABINET_001",
                "msg1", 1700000000, "nonce01", "{\"lock_id\":1}");
            Assert.Equal(a, b);
            Assert.Equal(64, a.Length);
        }

        [Fact]
        public void CompactData_Null_IsEmptyObject()
        {
            Assert.Equal("{}", MessageHmac.CompactData(null));
        }

        [Fact]
        public void IsSensitive_MatchesManagementCommands()
        {
            Assert.True(MessageHmac.IsSensitive(Protocol.CmdControlLock));
            Assert.True(MessageHmac.IsSensitive(Protocol.CmdRestoreFingerprint));
            Assert.True(MessageHmac.IsSensitive(Protocol.CmdDeleteUserPermission));
            Assert.False(MessageHmac.IsSensitive(Protocol.CmdHeartbeat));
        }
    }
}
