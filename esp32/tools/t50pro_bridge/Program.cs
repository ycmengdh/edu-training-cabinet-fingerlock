using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using Supvan.T50PRO.SDK;

namespace CabinetT50ProBridge
{
    internal sealed class BridgeRequest
    {
        public string command { get; set; }
        public string device_path { get; set; }
        public string label_text { get; set; }
        public int width_mm { get; set; }
        public int height_mm { get; set; }
        public int direction { get; set; }
        public int margin_left_mm { get; set; }
        public int margin_top_mm { get; set; }
        public int gap_mm { get; set; }
        public int speed { get; set; }
        public int deepness { get; set; }
        public string font_name { get; set; }
        public string font_size_mm { get; set; }
        public int timeout_seconds { get; set; }
    }

    internal sealed class BridgeResponse
    {
        public bool ok { get; set; }
        public string error { get; set; }
        public List<string> devices { get; set; }
        public int state { get; set; }
        public string state_name { get; set; }
        public string description { get; set; }
    }

    internal static class Program
    {
        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer();

        private static int Main()
        {
            Console.InputEncoding = Encoding.UTF8;
            Console.OutputEncoding = Encoding.UTF8;
            try
            {
                string input = Console.In.ReadToEnd();
                BridgeRequest request = Json.Deserialize<BridgeRequest>(input);
                if (request == null || String.IsNullOrWhiteSpace(request.command))
                {
                    return WriteError("缺少 command");
                }

                BridgeResponse response;
                switch (request.command.Trim().ToLowerInvariant())
                {
                    case "devices":
                        response = ListDevices();
                        break;
                    case "status":
                        response = GetStatus(request.device_path);
                        break;
                    case "print":
                        response = Print(request);
                        break;
                    default:
                        return WriteError("不支持的命令: " + request.command);
                }

                Console.WriteLine(Json.Serialize(response));
                return response.ok ? 0 : 1;
            }
            catch (Exception exception)
            {
                return WriteError(exception.GetBaseException().Message);
            }
        }

        private static BridgeResponse ListDevices()
        {
            List<string> devices = T50PROPrintUtil.GetDevicePaths();
            return new BridgeResponse
            {
                ok = true,
                error = "",
                devices = devices ?? new List<string>()
            };
        }

        private static BridgeResponse GetStatus(string devicePath)
        {
            if (String.IsNullOrWhiteSpace(devicePath))
            {
                return Error("未选择 T50 Pro 打印机");
            }

            devicePath = ResolveDevicePath(devicePath);
            if (devicePath == null)
            {
                return Error("所选 T50 Pro 已断开，请重新检测");
            }

            PrintResult result = T50PROPrintUtil.GetPrintResult(devicePath);
            if (result == null)
            {
                return Error("无法读取打印机状态，请检查连接和电源");
            }

            return FromPrintResult(result, true);
        }

        private static BridgeResponse Print(BridgeRequest request)
        {
            if (String.IsNullOrWhiteSpace(request.device_path))
            {
                return Error("未选择 T50 Pro 打印机");
            }
            if (String.IsNullOrWhiteSpace(request.label_text))
            {
                return Error("标签内容不能为空");
            }

            string devicePath = ResolveDevicePath(request.device_path);
            if (devicePath == null)
            {
                return Error("所选 T50 Pro 已断开，请重新检测");
            }

            PrintResult ready = WaitUntilReady(devicePath, 3000);
            if (ready == null)
            {
                return Error("无法读取打印机状态，请检查连接和电源");
            }
            if (ready.State != DeviceState.Waiting)
            {
                return FromPrintResult(ready, false);
            }

            SDKSPParamter parameters = BuildParameters(request);
            T50PROPrintUtil.DoPrint(parameters, devicePath);

            // The vendor demo intentionally ignores DoPrint's Boolean result and
            // does not poll afterward. Some firmware reports false or AbortPrint
            // after the label has physically printed, so only preflight failures
            // and thrown SDK exceptions are treated as failures here.
            return new BridgeResponse
            {
                ok = true,
                error = "",
                description = "打印任务已发送"
            };
        }

        private static PrintResult WaitUntilReady(string devicePath, int timeoutMs)
        {
            DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            PrintResult latest = null;
            do
            {
                latest = T50PROPrintUtil.GetPrintResult(devicePath);
                if (latest == null || latest.State == DeviceState.Waiting)
                {
                    return latest;
                }
                Thread.Sleep(200);
            }
            while (DateTime.UtcNow < deadline);
            return latest;
        }

        private static string ResolveDevicePath(string requestedPath)
        {
            List<string> devices = T50PROPrintUtil.GetDevicePaths();
            if (devices == null)
            {
                return null;
            }
            foreach (string device in devices)
            {
                if (String.Equals(device, requestedPath, StringComparison.OrdinalIgnoreCase))
                {
                    return device;
                }
            }
            return null;
        }

        private static SDKSPParamter BuildParameters(BridgeRequest request)
        {
            int width = request.width_mm > 0 ? request.width_mm : 50;
            int height = request.height_mm > 0 ? request.height_mm : 30;
            int direction = Clamp(request.direction, 0, 3, 3);
            decimal textHeight = Math.Max(5, Math.Min(10, height - 4));
            decimal x = 2;
            decimal y = Math.Max(1, (height - textHeight) / 2m);
            decimal drawWidth = width - 4;
            if (direction == 3)
            {
                decimal physicalLeft = Math.Max(-height, Math.Min(height, request.margin_left_mm));
                decimal physicalTop = Math.Max(-width, Math.Min(width, request.margin_top_mm));
                x = physicalTop;
                y = height - textHeight - physicalLeft;
                drawWidth = width;
            }

            return new SDKSPParamter
            {
                PrintSet = new SDKPrintSet
                {
                    Copy = 1,
                    Deepness = Clamp(request.deepness, 0, 9, 4),
                    DPI = 8f,
                    Direction = direction,
                    Gap = request.gap_mm > 0 ? request.gap_mm : 3,
                    Speed = Clamp(request.speed, 20, 60, 40),
                    Width = width,
                    Height = height,
                    PaperType = 1,
                    MaxDotValue = 384,
                    OffsetH = 0,
                    OffsetV = 0,
                    OneByOne = true
                },
                PrintPages = new List<SDKPrintPage>
                {
                    new SDKPrintPage
                    {
                        Repeat = 1,
                        DrawObjects = new List<SDKPrintPageDrawObject>
                        {
                            new SDKPrintPageDrawObject
                            {
                                AntiColor = false,
                                X = x,
                                Y = y,
                                Width = drawWidth,
                                Height = textHeight,
                                Content = request.label_text.Trim(),
                                FontName = String.IsNullOrWhiteSpace(request.font_name)
                                    ? "Microsoft YaHei"
                                    : request.font_name.Trim(),
                                FontStyle = 1,
                                Align = 1,
                                FontSize = String.IsNullOrWhiteSpace(request.font_size_mm)
                                    ? "3"
                                    : request.font_size_mm.Trim(),
                                AutoReturn = false,
                                Format = "TEXT"
                            }
                        }
                    }
                }
            };
        }

        private static int Clamp(int value, int minimum, int maximum, int fallback)
        {
            if (value < minimum || value > maximum)
            {
                return fallback;
            }
            return value;
        }

        private static BridgeResponse FromPrintResult(PrintResult result, bool ok)
        {
            string message = JoinMessage(result);
            return new BridgeResponse
            {
                ok = ok,
                error = ok ? "" : (String.IsNullOrWhiteSpace(message) ? "打印机未就绪" : message),
                state = (int)result.State,
                state_name = result.State.ToString(),
                description = message
            };
        }

        private static string JoinMessage(PrintResult result)
        {
            string description = result.PrintDes ?? "";
            string error = result.ErrorMsg ?? "";
            if (String.IsNullOrWhiteSpace(description))
            {
                return error.Trim();
            }
            if (String.IsNullOrWhiteSpace(error))
            {
                return description.Trim();
            }
            return description.Trim() + ": " + error.Trim();
        }

        private static BridgeResponse Error(string message)
        {
            return new BridgeResponse
            {
                ok = false,
                error = message ?? "未知错误",
                devices = new List<string>()
            };
        }

        private static int WriteError(string message)
        {
            Console.WriteLine(Json.Serialize(Error(message)));
            return 1;
        }
    }
}
