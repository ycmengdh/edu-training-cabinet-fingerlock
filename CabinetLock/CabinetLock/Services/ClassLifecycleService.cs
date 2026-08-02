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

            ClassLifecycleResult cleanup = await CleanupCabinetsAsync(
                plan, progress, cancellationToken).ConfigureAwait(false);
            if (!cleanup.Success) return cleanup;

            progress?.Report(new ClassLifecycleProgress(95, "正在提交班级停用状态"));
            if (!App.ClassService.SetEnabled(plan.ClassInfo.ClassId, false))
                return ClassLifecycleResult.Failed("柜机已清理，但班级停用状态保存失败，请重试");
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
            string[] offline = deviceIds.Where(id => !online.Contains(id)).ToArray();
            if (offline.Length > 0)
                return ClassLifecycleResult.Failed("以下已分配柜机离线，无法完成恢复：" + string.Join("、", offline));

            var failures = new List<string>();
            for (int index = 0; index < deviceIds.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string deviceId = deviceIds[index];
                int basePercent = 10 + index * 85 / deviceIds.Length;
                var deviceProgress = new Progress<string>(message => progress?.Report(
                    new ClassLifecycleProgress(basePercent, $"{deviceId}：{message}")));
                CabinetDataSyncResult result = await App.CabinetSyncService.SyncCabinetDataAsync(
                    deviceId, deviceProgress, cancellationToken).ConfigureAwait(false);
                if (!result.Success) failures.Add($"{deviceId}：{result.FormatForDisplay()}");
                progress?.Report(new ClassLifecycleProgress(
                    10 + (index + 1) * 85 / deviceIds.Length,
                    $"已处理柜机 {index + 1}/{deviceIds.Length}"));
            }

            if (failures.Count > 0)
                return ClassLifecycleResult.Failed("部分柜机恢复失败，可直接重试", failures);
            progress?.Report(new ClassLifecycleProgress(100, "班级已启用，全部柜机数据已校验"));
            return ClassLifecycleResult.Succeeded("班级启用与柜机恢复完成");
        }

        private async Task<ClassLifecycleResult> DeleteAsync(
            ClassLifecyclePlan plan, IProgress<ClassLifecycleProgress>? progress,
            CancellationToken cancellationToken)
        {
            ClassLifecycleResult cleanup = await CleanupCabinetsAsync(
                plan, progress, cancellationToken).ConfigureAwait(false);
            if (!cleanup.Success) return cleanup;

            progress?.Report(new ClassLifecycleProgress(88, "正在清理学生、权限和指纹模板"));
            HashSet<string> studentIds = plan.Students.Select(user => user.UserId)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            List<User> remainingUsers = plan.AllUsers.Where(user => !studentIds.Contains(user.UserId))
                .ToList();
            foreach (User teacher in remainingUsers.Where(user =>
                         user.IsResponsibleForClass(plan.ClassInfo.ClassId)))
            {
                teacher.SetResponsibleClassIds(teacher.GetResponsibleClassIds().Where(id =>
                    !string.Equals(id, plan.ClassInfo.ClassId, StringComparison.OrdinalIgnoreCase)));
                teacher.UpdateTime = DateTime.Now;
            }
            if (!_root.Save("users", remainingUsers))
                return ClassLifecycleResult.Failed("柜机已清理，但学生数据保存失败，请重试");

            List<UserPermission> permissions = _root.Read<UserPermission>("permissions");
            permissions.RemoveAll(item => studentIds.Contains(item.UserId));
            if (!_root.Save("permissions", permissions))
                return ClassLifecycleResult.Failed("学生已清理，但权限数据保存失败，请重试");

            foreach (FingerprintTemplate template in plan.Templates.Where(item =>
                         !string.IsNullOrWhiteSpace(item.UserId) && studentIds.Contains(item.UserId)))
                App.FingerprintTemplateService.DeleteTemplate(template.FingerprintId);
            foreach (User student in plan.Students)
            {
                App.CabinetBindingService.RemoveFromAll(student.UserId);
                try { await App.SdStorageService.DeleteTemplateAsync(student.UserId).ConfigureAwait(false); }
                catch { }
            }

            progress?.Report(new ClassLifecycleProgress(97, "正在删除班级记录"));
            if (!App.ClassService.Delete(plan.ClassInfo.ClassId))
                return ClassLifecycleResult.Failed("学生已清理，但班级记录删除失败，请重试");

            progress?.Report(new ClassLifecycleProgress(100, "班级及关联学生数据已全部删除"));
            return ClassLifecycleResult.Succeeded("班级删除完成");
        }

        private static async Task<ClassLifecycleResult> CleanupCabinetsAsync(
            ClassLifecyclePlan plan, IProgress<ClassLifecycleProgress>? progress,
            CancellationToken cancellationToken)
        {
            string[] deviceIds = plan.DeviceFingerprints.Keys.OrderBy(id => id).ToArray();
            if (deviceIds.Length == 0)
                return ClassLifecycleResult.Succeeded("班级没有柜机分配，无需远端清理");

            HashSet<string> online = App.MeshBridge.GetOnlineDevices()
                .Where(device => device.IsOnline && !device.IsRoot)
                .Select(device => device.DeviceId)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            string[] offline = deviceIds.Where(id => !online.Contains(id)).ToArray();
            if (offline.Length > 0)
                return ClassLifecycleResult.Failed(
                    "必须先连接全部已分配柜机再重试。离线柜机：" + string.Join("、", offline));

            string[] studentIds = plan.Students.Select(user => user.UserId).ToArray();
            var failures = new List<string>();
            int totalSteps = deviceIds.Sum(id => 1 + plan.DeviceFingerprints[id].Count);
            int completed = 0;
            foreach (string deviceId in deviceIds)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report(new ClassLifecycleProgress(
                    completed * 85 / Math.Max(1, totalSteps), $"{deviceId}：正在撤销班级权限"));
                BroadcastCommandResult permissionResult = await Task.Run(() =>
                    App.CabinetSyncService.SyncCabinetPermissionsExcludingUsers(deviceId, studentIds),
                    cancellationToken).ConfigureAwait(false);
                completed++;
                if (!permissionResult.Success)
                {
                    failures.Add($"{deviceId}：权限清理失败，{permissionResult.ErrorMessage}");
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
                        failures.Add($"{deviceId} / 指纹 #{fingerprintId}：{deleted.ErrorMessage}");
                }
            }

            return failures.Count == 0
                ? ClassLifecycleResult.Succeeded("全部柜机已确认清理")
                : ClassLifecycleResult.Failed("部分柜机清理失败，可直接重试", failures);
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
    }
}
