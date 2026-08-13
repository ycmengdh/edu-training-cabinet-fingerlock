namespace CabinetLock.Tests;

public class CabinetBindingDeletionTests
{
    [Fact]
    public void RecordedAssignments_EmptyStudentBinding_DoesNotTargetAnyCabinet()
    {
        var student = new User
        {
            UserId = "S001",
            Role = "student",
            AssignedDeviceIds = new List<string>(),
            CabinetAssignments = new List<CabinetAssignment>()
        };

        HashSet<string> assigned = new CabinetBindingService()
            .GetRecordedAssignedDeviceIds(student, new[] { "CAB_01", "CAB_02" });

        Assert.Empty(assigned);
    }

    [Fact]
    public void RecordedAssignments_OnlyTargetsExplicitCabinetBindings()
    {
        var student = new User
        {
            UserId = "S001",
            Role = "student",
            CabinetAssignments = new List<CabinetAssignment>
            {
                new() { DeviceId = "CAB_02", FingerprintIds = new List<int> { 11 } }
            }
        };

        HashSet<string> assigned = new CabinetBindingService()
            .GetRecordedAssignedDeviceIds(student, new[] { "CAB_01", "CAB_02" });

        Assert.Equal(new[] { "CAB_02" }, assigned);
    }
}
