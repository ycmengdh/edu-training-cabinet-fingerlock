namespace CabinetLock.Tests;

public class CommunicationCoordinatorTests
{
    [Fact]
    public async Task ExclusiveOperations_DoNotOverlap()
    {
        var coordinator = new CommunicationCoordinator();
        var firstEntered = Signal();
        var releaseFirst = Signal();
        var secondEntered = Signal();
        var order = new List<string>();

        Task first = coordinator.RunExclusiveAsync(
            CommunicationOperationKind.CabinetSync, "first", "CAB_1",
            async _ =>
            {
                order.Add("first-start");
                firstEntered.TrySetResult();
                await releaseFirst.Task;
                order.Add("first-end");
            });
        await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Task second = coordinator.RunExclusiveAsync(
            CommunicationOperationKind.SdSync, "second", "ROOT",
            _ =>
            {
                order.Add("second");
                secondEntered.TrySetResult();
                return Task.CompletedTask;
            });

        await Task.Delay(50);
        Assert.False(secondEntered.Task.IsCompleted);
        releaseFirst.TrySetResult();
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(new[] { "first-start", "first-end", "second" }, order);
        Assert.Equal(CommunicationMode.Normal, coordinator.Current.Mode);
    }

    [Fact]
    public async Task WaitingOta_TakesPriorityAtNextSafeBoundary()
    {
        var coordinator = new CommunicationCoordinator();
        var firstEntered = Signal();
        var releaseFirst = Signal();
        var secondEntered = Signal();
        var otaEntered = Signal();
        var releaseOta = Signal();
        var order = new List<string>();

        Task first = coordinator.RunExclusiveAsync(
            CommunicationOperationKind.CabinetSync, "active sync", "CAB_1",
            async _ =>
            {
                order.Add("sync-1");
                firstEntered.TrySetResult();
                await releaseFirst.Task;
            });
        await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Task second = coordinator.RunExclusiveAsync(
            CommunicationOperationKind.CabinetSync, "queued sync", "CAB_2",
            _ =>
            {
                order.Add("sync-2");
                secondEntered.TrySetResult();
                return Task.CompletedTask;
            });
        Task ota = coordinator.RunExclusiveAsync(
            CommunicationOperationKind.Ota, "ota", "ROOT",
            async _ =>
            {
                order.Add("ota");
                otaEntered.TrySetResult();
                await releaseOta.Task;
            });

        Assert.True(coordinator.IsOtaPendingOrActive);
        releaseFirst.TrySetResult();
        await otaEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(secondEntered.Task.IsCompleted);

        releaseOta.TrySetResult();
        await Task.WhenAll(first, second, ota).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(new[] { "sync-1", "ota", "sync-2" }, order);
    }

    [Fact]
    public async Task OtaMode_BlocksUnrelatedExternalTraffic()
    {
        var coordinator = new CommunicationCoordinator();
        var otaEntered = Signal();
        var releaseOta = Signal();

        Task ota = coordinator.RunExclusiveAsync(
            CommunicationOperationKind.Ota, "ota", "ROOT",
            async _ =>
            {
                otaEntered.TrySetResult();
                await releaseOta.Task;
            });
        await otaEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.False(coordinator.CanSend(Protocol.CmdReadStatus, out string reason));
        Assert.Contains("OTA", reason, StringComparison.OrdinalIgnoreCase);
        Assert.True(coordinator.CanSend(Protocol.CmdCabinetOtaStatus, out _));
        Assert.True(coordinator.CanSend(Protocol.CmdRegister, out _));
        Assert.False(coordinator.IsBackgroundTrafficAllowed);

        releaseOta.TrySetResult();
        await ota.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(coordinator.CanSend(Protocol.CmdReadStatus, out _));
    }

    [Fact]
    public async Task NestedNonOtaOperation_ReusesCurrentLease()
    {
        var coordinator = new CommunicationCoordinator();
        int calls = 0;

        await coordinator.RunExclusiveAsync(
            CommunicationOperationKind.CabinetSync, "cabinet", "CAB_1",
            async _ =>
            {
                calls++;
                await coordinator.RunExclusiveAsync(
                    CommunicationOperationKind.SdSync, "template", "ROOT",
                    _ =>
                    {
                        calls++;
                        return Task.CompletedTask;
                    });
            }).WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(2, calls);
        Assert.Equal(CommunicationMode.Normal, coordinator.Current.Mode);
    }

    [Fact]
    public async Task CancelledWaitingOta_ClearsPriorityBarrier()
    {
        var coordinator = new CommunicationCoordinator();
        var syncEntered = Signal();
        var releaseSync = Signal();
        using var cancellation = new CancellationTokenSource();

        Task sync = coordinator.RunExclusiveAsync(
            CommunicationOperationKind.CabinetSync, "sync", "CAB_1",
            async _ =>
            {
                syncEntered.TrySetResult();
                await releaseSync.Task;
            });
        await syncEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Task ota = coordinator.RunExclusiveAsync(
            CommunicationOperationKind.Ota, "ota", "ROOT",
            _ => Task.CompletedTask,
            cancellation.Token);
        Assert.True(coordinator.IsOtaPendingOrActive);

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await ota);
        Assert.False(coordinator.IsOtaPendingOrActive);

        releaseSync.TrySetResult();
        await sync.WaitAsync(TimeSpan.FromSeconds(2));
    }

    private static TaskCompletionSource Signal() => new(
        TaskCreationOptions.RunContinuationsAsynchronously);
}
