namespace CabinetLock
{
    public enum ClassLifecycleAction
    {
        Enable,
        Disable,
        Delete
    }

    public sealed class ClassLifecycleService
    {
        private const int DeleteCommandTimeoutMs = 5_000;
        private static readonly SemaphoreSlim OperationLock = new(1, 1);
        private readonly RootDataService _root = new();

        public async Task<ClassLifecycleResult> ExecuteAsync(
            string classId, ClassLifecycleAction action,
            IProgress<ClassLifecycleProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            await OperationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ClassInfo? classInfo = App.ClassService.Get(classId);
                if (classInfo == null)
                {
                    return action == ClassLifecycleAction.Delete
                        ? ClassLifecycleResult.Succeeded("班级已删除")
                        : ClassLifecycleResult.Failed("班级不存在或已被删除");
                }

                ClassLifecyclePlan plan = BuildPlan(classInfo);
                return action switch
                {
                    ClassLifecycleAction.Enable => await EnableAsync(plan, progress, cancellationToken)
                        .ConfigureAwait(false),
                    ClassLifecycleAction.Disable => await DisableAsync(plan, progress, cancellationToken)
                        .ConfigureAwait(false),
                    _ => await DeleteAsync(plan, progress, cancellationToken).ConfigureAwait(false)
                };
            }
            catch (OperationCanceledException)
            {
                return ClassLifecycleResult.Failed("操作已取消");
            }
            catch (Exception ex)
            {
                return ClassLifecycleResult.Failed(ex.Message);
            }
            finally
            {
                OperationLock.Release();
            }
        }

        private static ClassLifecyclePlan BuildPlan(ClassInfo classInfo)
        {
            List<User> allUsers = App.UserService.GetAllUsers();
            List<User> students = allUsers.Where(user =>
                    string.Equals(user.Role, "student", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(user.ClassId, classInfo.ClassId, StringComparison.OrdinalIgnoreCase))
                .ToList();
            List<Device> devices = App.DeviceService.GetAllDevices()
                .Where(device => !DeviceService.IsTrueRoot(device))
                .ToList();
            List<FingerprintTemplate> templates = BusinessDatabase.ReadAllFpTemplateMetas();
            var deviceFingerprints = new Dictionary<string, HashSet<int>>(
                StringComparer.OrdinalIgnoreCase);

            foreach (User student in students)
            {
                int[] fingerprintIds = templates.Where(item => string.Equals(
                        item.UserId, student.UserId, StringComparison.OrdinalIgnoreCase))
                    .Select(item => item.FingerprintId)
                    .Append(student.FingerprintId ?? -1)
                    .Where(id => id > 0)
                    .Distinct()
                    .ToArray();
                foreach (string deviceId in App.CabinetBindingService.GetAssignedDeviceIds(
                             student, devices.Select(device => device.DeviceId)))
                {
                    if (!deviceFingerprints.TryGetValue(deviceId, out HashSet<int>? ids))
                    {
                        ids = new HashSet<int>();
                        deviceFingerprints[deviceId] = ids;
                    }
                    ids.UnionWith(fingerprintIds);
                }
            }

            return new ClassLifecyclePlan(classInfo, allUsers, students, devices,
                templates, deviceFingerprints);
        }

        private static async Task<ClassLifecycleResult> DisableAsync(
            ClassLifecyclePlan plan, IProgress<ClassLifecycleProgress>? progress,
            CancellationToken cancellationToken)
        {
            if (!plan.ClassInfo.Enabled)
                return ClassLifecycleResult.Succeeded("班级已经停用");

            if (!App.ClassService.SetEnabled(plan.ClassInfo.ClassId, false))
                return ClassLifecycleResult.Failed("班级停用状态保存失败，请重试");
            ClassLifecycleResult cleanup = await CleanupCabinetsAsync(
                plan, progress, cancellationToken).ConfigureAwait(false);
            if (!cleanup.Success) return cleanup;
            progress?.Report(new ClassLifecycleProgress(100, "班级已停用，柜机权限与指纹均已清理"));
            return ClassLifecycleResult.Succeeded("班级停用完成");
        }

        private static async Task<ClassLifecycleResult> EnableAsync(
            ClassLifecyclePlan plan, IProgress<ClassLifecycleProgress>? progress,
            CancellationToken cancellationToken)
        {
            if (!plan.ClassInfo.Enabled)
            {
                progress?.Report(new ClassLifecycleProgress(5, "正在启用班级"));
                if (!App.ClassService.SetEnabled(plan.ClassInfo.ClassId, true))
                    return ClassLifecycleResult.Failed("班级启用状态保存失败");
            }

            string[] deviceIds = plan.DeviceFingerprints.Keys.OrderBy(id => id).ToArray();
            if (deviceIds.Length == 0)
                return ClassLifecycleResult.Succeeded("班级已启用，暂无柜机分配需要恢复");

            HashSet<string> online = App.MeshBridge.GetOnlineDevices()
                .Where(device => device.IsOnline && !device.IsRoot)
                .Select(device => device.DeviceId)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var failures = new List<string>();
            int queued = 0;
            for (int index = 0; index < deviceIds.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string deviceId = deviceIds[index];
                if (!online.Contains(deviceId))
                {
                    App.CabinetSyncQueueService.EnqueueCabinet(
                        deviceId, "班级启用后恢复柜机数据");
                    queued++;
                    progress?.Report(new ClassLifecycleProgress(
                        10 + (index + 1) * 85 / deviceIds.Length,
                        $"{deviceId} 离线，已加入待同步队列"));
                    continue;
                }
                int basePercent = 10 + index * 85 / deviceIds.Length;
                var deviceProgress = new Progress<string>(message => progress?.Report(
                    new ClassLifecycleProgress(basePercent, $"{deviceId}：{message}")));
                CabinetDataSyncResult result = await App.CabinetSyncService.SyncCabinetDataAsync(
                    deviceId, deviceProgress, cancellationToken).ConfigureAwait(false);
                if (!result.Success)
                {
                    failures.Add($"{deviceId}：{result.FormatForDisplay()}");
                    App.CabinetSyncQueueService.EnqueueCabinet(
                        deviceId, "班级启用同步失败后重试");
                    queued++;
                }
                progress?.Report(new ClassLifecycleProgress(
                    10 + (index + 1) * 85 / deviceIds.Length,
                    $"已处理柜机 {index + 1}/{deviceIds.Length}"));
            }

            if (queued > 0) App.CabinetSyncQueueService.Trigger();
            progress?.Report(new ClassLifecycleProgress(100, "班级已启用，全部柜机数据已校验"));
            return ClassLifecycleResult.Succeeded(queued == 0
                ? "班级启用与柜机恢复完成"
                : $"班级已启用，{queued} 台柜机将在在线后继续同步");
        }

        private async Task<ClassLifecycleResult> DeleteAsync(
            ClassLifecyclePlan plan, IProgress<ClassLifecycleProgress>? progress,
            CancellationToken cancellationToken)
        {
            string[] knownDeviceIds = plan.Devices.Select(device => device.DeviceId).ToArray();
            var details = new List<string>();
            int deletedStudents = 0;
            int skippedStudents = 0;

            for (int studentIndex = 0; studentIndex < plan.Students.Count; studentIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                User student = plan.Students[studentIndex];
                string studentLabel = $"{student.Name}（{student.DisplayId}）";
                int basePercent = plan.Students.Count == 0
                    ? 0 : studentIndex * 90 / plan.Students.Count;

                string[] assignedDeviceIds = App.CabinetBindingService
                    .GetRecordedAssignedDeviceIds(student, knownDeviceIds)
                    .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                bool hasCabinetBindings = assignedDeviceIds.Length > 0;
                if (hasCabinetBindings)
                {
                    progress?.Report(new ClassLifecycleProgress(
                        basePercent,
                        $"正在检查学生 {studentLabel} 的柜机（{studentIndex + 1}/{plan.Students.Count}）"));
                }

                HashSet<string> online = !hasCabinetBindings
                    ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                    : App.MeshBridge.GetOnlineDevices()
                        .Where(device => device.IsOnline && !device.IsRoot)
                        .Select(device => device.DeviceId)
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);
                string[] offlineDeviceIds = assignedDeviceIds
                    .Where(deviceId => !online.Contains(deviceId))
                    .ToArray();
                if (offlineDeviceIds.Length > 0)
                {
                    skippedStudents++;
                    details.Add($"{studentLabel}：柜机 {string.Join("、", offlineDeviceIds)} 离线，已跳过");
                    progress?.Report(new ClassLifecycleProgress(
                        (studentIndex + 1) * 90 / Math.Max(1, plan.Students.Count),
                        $"已跳过 {studentLabel}：{offlineDeviceIds.Length} 台柜机离线"));
                    continue;
                }

                int[] fingerprintIds = plan.Templates.Where(template => string.Equals(
                        template.UserId, student.UserId, StringComparison.OrdinalIgnoreCase))
                    .Select(template => template.FingerprintId)
                    .Append(student.FingerprintId ?? -1)
                    .Where(id => id > 0)
                    .Distinct()
                    .ToArray();
                uint currentVersion = hasCabinetBindings
                    ? CabinetSyncService.GetExpectedPermissionVersion()
                    : 0;
                string? remoteFailure = null;
                foreach (string deviceId in assignedDeviceIds)
                {
                    progress?.Report(new ClassLifecycleProgress(
                        basePercent, $"{studentLabel}：正在删除 {deviceId} 的权限"));
                    CommandResult permissionDeleted = await App.CommandService
                        .DeleteUserPermissionAsync(
                            deviceId, student.UserId, currentVersion, DeleteCommandTimeoutMs)
                        .ConfigureAwait(false);
                    if (!permissionDeleted.Success)
                    {
                        remoteFailure = $"{deviceId} 权限删除失败：{permissionDeleted.ErrorMessage}";
                        break;
                    }

                    foreach (int fingerprintId in fingerprintIds)
                    {
                        progress?.Report(new ClassLifecycleProgress(
                            basePercent, $"{studentLabel}：正在删除 {deviceId} 的指纹 #{fingerprintId}"));
                        CommandResult fingerprintDeleted = await App.CommandService.SendAsync(
                            deviceId,
                            Message.Create(Protocol.CmdDeleteFingerprint, deviceId,
                                new { fingerprint_id = fingerprintId }),
                            DeleteCommandTimeoutMs)
                            .ConfigureAwait(false);
                        if (!fingerprintDeleted.Success)
                        {
                            remoteFailure = $"{deviceId} 指纹 #{fingerprintId} 删除失败：" +
                                fingerprintDeleted.ErrorMessage;
                            break;
                        }
                    }
                    if (remoteFailure != null) break;
                }

                if (remoteFailure != null)
                {
                    skippedStudents++;
                    details.Add($"{studentLabel}：{remoteFailure}，本地学生数据已保留");
                    progress?.Report(new ClassLifecycleProgress(
                        (studentIndex + 1) * 90 / Math.Max(1, plan.Students.Count),
                        $"已跳过 {studentLabel}：柜机未确认删除"));
                    continue;
                }

                if (hasCabinetBindings || fingerprintIds.Length > 0)
                {
                    progress?.Report(new ClassLifecycleProgress(
                        basePercent, hasCabinetBindings
                            ? $"{studentLabel}：柜机已清理，正在删除本地学生数据"
                            : $"{studentLabel}：正在删除本地学生与指纹数据"));
                }
                if (!App.UserService.DeleteUser(student.UserId, enqueueCabinetCleanup: false))
                {
                    skippedStudents++;
                    details.Add($"{studentLabel}：本地学生数据删除失败，已保留");
                    continue;
                }

                foreach (int fingerprintId in fingerprintIds)
                    App.FingerprintTemplateService.DeleteTemplate(fingerprintId);
                App.CabinetBindingService.RemoveFromAll(student.UserId);
                if (fingerprintIds.Length > 0)
                {
                    try
                    {
                        await App.SdStorageService.DeleteTemplateAsync(
                                student.UserId, DeleteCommandTimeoutMs)
                            .ConfigureAwait(false);
                    }
                    catch { }
                }
                deletedStudents++;
                details.Add($"{studentLabel}：删除完成");
                int processedCount = studentIndex + 1;
                if (hasCabinetBindings || fingerprintIds.Length > 0 ||
                    processedCount % 25 == 0 || processedCount == plan.Students.Count)
                {
                    progress?.Report(new ClassLifecycleProgress(
                        processedCount * 90 / Math.Max(1, plan.Students.Count),
                        hasCabinetBindings || fingerprintIds.Length > 0
                            ? $"已删除学生 {studentLabel}（{processedCount}/{plan.Students.Count}）"
                            : $"正在逐个删除本地学生（{processedCount}/{plan.Students.Count}）"));
                }
            }

            bool classDeleted = false;
            List<User> remainingStudents = App.UserService.GetAllUsers().Where(user =>
                    string.Equals(user.Role, "student", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(user.ClassId, plan.ClassInfo.ClassId, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (remainingStudents.Count == 0)
            {
                progress?.Report(new ClassLifecycleProgress(94, "所有学生均已删除，正在删除班级"));
                if (!DetachTeachersFromClass(plan.ClassInfo.ClassId))
                    return ClassLifecycleResult.Failed("学生已删除，但教师班级关系保存失败，请重试", details);
                if (!App.ClassService.Delete(plan.ClassInfo.ClassId))
                    return ClassLifecycleResult.Failed("学生已删除，但班级记录删除失败，请重试", details);
                classDeleted = true;
            }

            progress?.Report(new ClassLifecycleProgress(100, classDeleted
                ? "学生与班级删除完成"
                : $"已删除 {deletedStudents} 名学生，跳过 {skippedStudents} 名，班级已保留"));
            if (classDeleted)
                return ClassLifecycleResult.Succeeded($"班级删除完成，共删除 {deletedStudents} 名学生");
            if (deletedStudents > 0)
                return ClassLifecycleResult.Partial(
                    $"已删除 {deletedStudents} 名学生，跳过 {skippedStudents} 名；班级已保留",
                    details);
            return ClassLifecycleResult.Skipped(
                $"{skippedStudents} 名学生均未删除，班级已保留", details);
        }

        private bool DetachTeachersFromClass(string classId)
        {
            List<User> users = App.UserService.GetAllUsers();
            bool changed = false;
            foreach (User teacher in users.Where(user => user.IsResponsibleForClass(classId)))
            {
                teacher.SetResponsibleClassIds(teacher.GetResponsibleClassIds().Where(id =>
                    !string.Equals(id, classId, StringComparison.OrdinalIgnoreCase)));
                teacher.UpdateTime = DateTime.Now;
                changed = true;
            }
            return !changed || _root.Save("users", users);
        }

        private static async Task<ClassLifecycleResult> CleanupCabinetsAsync(
            ClassLifecyclePlan plan, IProgress<ClassLifecycleProgress>? progress,
            CancellationToken cancellationToken, bool enqueueRetries = true)
        {
            string[] deviceIds = plan.DeviceFingerprints.Keys.OrderBy(id => id).ToArray();
            if (deviceIds.Length == 0)
                return ClassLifecycleResult.Succeeded("班级没有柜机分配，无需远端清理");

            HashSet<string> online = App.MeshBridge.GetOnlineDevices()
                .Where(device => device.IsOnline && !device.IsRoot)
                .Select(device => device.DeviceId)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            string[] studentIds = plan.Students.Select(user => user.UserId).ToArray();
            var failures = new List<string>();
            var queuedDevices = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int totalSteps = deviceIds.Sum(id => 1 + plan.DeviceFingerprints[id].Count);
            int completed = 0;
            foreach (string deviceId in deviceIds)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!online.Contains(deviceId))
                {
                    if (enqueueRetries)
                        App.CabinetSyncQueueService.EnqueueCabinet(
                            deviceId, "班级停用或删除后的柜机清理");
                    queuedDevices.Add(deviceId);
                    failures.Add($"{deviceId}：柜机离线，未清理权限与指纹");
                    completed += 1 + plan.DeviceFingerprints[deviceId].Count;
                    progress?.Report(new ClassLifecycleProgress(
                        completed * 85 / Math.Max(1, totalSteps),
                        $"{deviceId} 离线，已加入待同步队列"));
                    continue;
                }
                progress?.Report(new ClassLifecycleProgress(
                    completed * 85 / Math.Max(1, totalSteps), $"{deviceId}：正在撤销班级权限"));
                BroadcastCommandResult permissionResult = await Task.Run(() =>
                    App.CabinetSyncService.SyncCabinetPermissionsExcludingUsers(deviceId, studentIds),
                    cancellationToken).ConfigureAwait(false);
                completed++;
                if (!permissionResult.Success)
                {
                    failures.Add($"{deviceId}：权限清理失败，{permissionResult.ErrorMessage}");
                    if (enqueueRetries)
                        App.CabinetSyncQueueService.EnqueueCabinet(
                            deviceId, "班级权限清理失败后重试");
                    queuedDevices.Add(deviceId);
                    continue;
                }

                foreach (int fingerprintId in plan.DeviceFingerprints[deviceId].OrderBy(id => id))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    progress?.Report(new ClassLifecycleProgress(
                        completed * 85 / Math.Max(1, totalSteps),
                        $"{deviceId}：正在删除指纹 #{fingerprintId}"));
                    CommandResult deleted = await App.CabinetSyncService
                        .DeleteFingerprintFromCabinetIdempotentAsync(deviceId, fingerprintId)
                        .ConfigureAwait(false);
                    completed++;
                    if (!deleted.Success)
                    {
                        failures.Add($"{deviceId} / 指纹 #{fingerprintId}：{deleted.ErrorMessage}");
                        if (enqueueRetries)
                            App.CabinetSyncQueueService.EnqueueCabinet(
                                deviceId, "班级指纹清理失败后核对");
                        queuedDevices.Add(deviceId);
                    }
                }
            }

            if (enqueueRetries && queuedDevices.Count > 0)
                App.CabinetSyncQueueService.Trigger();
            if (queuedDevices.Count == 0)
                return ClassLifecycleResult.Succeeded("全部柜机已确认清理");

            if (enqueueRetries)
                return ClassLifecycleResult.Succeeded(
                    $"{queuedDevices.Count} 台柜机未完成，已加入后台清理队列");

            return ClassLifecycleResult.Failed(
                $"{queuedDevices.Count} 台柜机离线或清理失败", failures);
        }

        private sealed record ClassLifecyclePlan(
            ClassInfo ClassInfo,
            List<User> AllUsers,
            List<User> Students,
            List<Device> Devices,
            List<FingerprintTemplate> Templates,
            Dictionary<string, HashSet<int>> DeviceFingerprints);
    }

    public sealed record ClassLifecycleProgress(int Percent, string Message);

    public sealed class ClassLifecycleResult
    {
        public bool Success { get; init; }
        public bool WasSkipped { get; init; }
        public bool IsPartial { get; init; }
        public string Message { get; init; } = "";
        public IReadOnlyList<string> Failures { get; init; } = Array.Empty<string>();

        public static ClassLifecycleResult Succeeded(string message) => new()
        {
            Success = true,
            Message = message
        };

        public static ClassLifecycleResult Failed(
            string message, IEnumerable<string>? failures = null) => new()
        {
            Message = message,
            Failures = failures?.ToArray() ?? Array.Empty<string>()
        };

        public static ClassLifecycleResult Skipped(
            string message, IEnumerable<string>? failures = null) => new()
        {
            WasSkipped = true,
            Message = message,
            Failures = failures?.ToArray() ?? Array.Empty<string>()
        };

        public static ClassLifecycleResult Partial(
            string message, IEnumerable<string>? failures = null) => new()
        {
            Success = true,
            IsPartial = true,
            Message = message,
            Failures = failures?.ToArray() ?? Array.Empty<string>()
        };
    }
}
