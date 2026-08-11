namespace CabinetLock
{
    public static class FingerprintEnrollmentPrompts
    {
        public static string GetHint(string? phase, int step = 0, int total = 6)
        {
            return NormalizePhase(phase) switch
            {
                "place_1" => "请按下手指并保持不动，开始第 1 次采集",
                "lift_1" => "第 1 次采集完成，请松开手指",
                "place_2" => "请再次按下同一根手指，开始第 2 次采集",
                "lift_2" => "第 2 次采集完成，请松开手指",
                "place_3" => "请再次按下同一根手指，开始第 3 次采集",
                "lift_3" => "第 3 次采集完成，请松开手指",
                "place_4" => "请最后一次按下同一根手指，完成第 4 次采集",
                "store" or "storing" => "采集完成，正在生成指纹模板，请松开手指",
                "verify_lift_1" => "模板已生成，请先松开手指，准备第一次验证",
                "verify_place_1" => "请按下同一根手指，进行第一次验证",
                "verify_retry_lift_1" => "第一次验证未识别，请松开手指并调整按压位置后重试",
                "verify_lift_2" => "第一次验证通过，请松开手指",
                "verify_place_2" => "请再次按下同一根手指，进行第二次验证",
                "verify_retry_lift_2" => "第二次验证未识别，请松开手指并调整按压位置后重试",
                "verify_1" => "请先松开手指，完全松开后再次按下，进行第一次验证",
                "verify_2" => "第一次验证通过，请松开手指，完全松开后再次按下，进行第二次验证",
                "done" or "success" => "两次验证通过，指纹录入成功",
                "failed" => "指纹录入未完成，请重新尝试",
                _ => step > 0 && total > 0
                    ? $"正在处理指纹，请按提示操作（{step}/{total}）"
                    : "正在处理指纹，请按提示操作"
            };
        }

        public static bool IsVerificationPhase(string? phase) =>
            NormalizePhase(phase).StartsWith("verify", StringComparison.Ordinal);

        public static int GetDisplayDelayMilliseconds(string? phase) =>
            NormalizePhase(phase) switch
            {
                "place_2" or "place_3" or "place_4" => 300,
                "verify_place_1" or "verify_place_2" => 350,
                _ => 0
            };

        public static int GetProgressValue(string? phase, int step, int total)
        {
            int safeTotal = Math.Max(0, total);
            int safeStep = safeTotal > 0
                ? Math.Clamp(step, 0, safeTotal)
                : Math.Max(0, step);
            return NormalizePhase(phase) switch
            {
                "verify_lift_2" or "verify_place_2" or
                "verify_retry_lift_2" or "verify_2" when safeTotal > 0 =>
                    Math.Min(safeStep, safeTotal - 1),
                _ => safeStep
            };
        }

        public static string LocalizeError(string? message)
        {
            string error = message?.Trim() ?? "";
            if (error.Length == 0) return "柜机未能完成指纹录入，请重新尝试";
            if (error.Any(IsChineseCharacter)) return error;

            string normalized = error.ToLowerInvariant();
            if (normalized.Contains("user_cancel") || normalized == "cancelled")
                return "已取消本次指纹录入";
            if (normalized.Contains("timeout"))
                return "等待手指操作超时，请重新录入";
            if (normalized.Contains("did not match"))
                return "多次采集到的指纹不一致，请使用同一根手指重新录入";
            if (normalized.Contains("verification failed after retries"))
                return "连续 3 次验证未通过，请调整手指位置后重新录入";
            if (normalized.Contains("verification failed"))
                return "指纹验证未通过，请重新录入";
            if (normalized.Contains("store failed"))
                return "指纹模板生成或保存失败，请重新录入";
            if (normalized.Contains("busy"))
                return "柜机正在执行其他操作，请稍后再试";
            if (normalized.Contains("not ready") || normalized.Contains("unavailable") ||
                normalized.Contains("communication") || normalized.Contains("uart"))
                return "指纹模块尚未就绪，请检查柜机连接后重试";
            if (normalized.Contains("invalid target") || normalized.Contains("invalid fingerprint"))
                return "指纹编号无效，请刷新用户信息后重试";
            if (normalized.Contains("message id"))
                return "录入请求创建失败，请稍后重试";
            return "柜机未能完成指纹录入，请重新尝试";
        }

        public static string EnhanceFailureForPhase(string? message, string? phase)
        {
            string localized = LocalizeError(message);
            if (!string.Equals(localized, "柜机未能完成指纹录入，请重新尝试",
                    StringComparison.Ordinal))
                return localized;

            string normalizedPhase = NormalizePhase(phase);
            if (normalizedPhase.StartsWith("verify", StringComparison.Ordinal))
                return "指纹验证未通过，请调整手指按压位置后重新录入";
            if (normalizedPhase is "store" or "storing")
                return "指纹模板生成失败，请重新录入";
            if (normalizedPhase.StartsWith("place", StringComparison.Ordinal) ||
                normalizedPhase.StartsWith("lift", StringComparison.Ordinal))
                return "本次指纹采集未完成，请保持使用同一根手指并重新尝试";
            return localized;
        }

        private static bool IsChineseCharacter(char value) =>
            value is >= '\u3400' and <= '\u9fff';

        private static string NormalizePhase(string? phase) =>
            phase?.Trim().ToLowerInvariant() ?? "";
    }
}
