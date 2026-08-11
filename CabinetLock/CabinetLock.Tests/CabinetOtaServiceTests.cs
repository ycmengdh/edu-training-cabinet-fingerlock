using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace CabinetLock.Tests;

public class CabinetOtaServiceTests
{
    [Fact]
    public void InspectFirmware_AcceptsCabinetEsp32S3Image()
    {
        byte[] image = CreateImage("cabinet_node_idf", "3.2.1-idf");

        CabinetFirmwareInfo result = CabinetOtaService.InspectFirmware(image);

        Assert.Equal("cabinet_node_idf", result.ProjectName);
        Assert.Equal("3.2.1-idf", result.Version);
        Assert.Equal("cabinet-v1", result.HardwareVersion);
        Assert.Equal(image.Length, result.ImageSize);
        Assert.Equal(64, result.Sha256.Length);
    }

    [Fact]
    public void InspectFirmware_RejectsRootImage()
    {
        byte[] image = CreateImage("cabinet_root_idf", "3.2.1-idf");

        InvalidDataException error = Assert.Throws<InvalidDataException>(
            () => CabinetOtaService.InspectFirmware(image));

        Assert.Contains("cabinet_node_idf", error.Message);
    }

    [Theory]
    [InlineData(CmdIds.CabinetOtaBegin, Protocol.CmdCabinetOtaBegin)]
    [InlineData(CmdIds.CabinetOtaChunk, Protocol.CmdCabinetOtaChunk)]
    [InlineData(CmdIds.CabinetOtaCommit, Protocol.CmdCabinetOtaCommit)]
    [InlineData(CmdIds.CabinetOtaStart, Protocol.CmdCabinetOtaStart)]
    [InlineData(CmdIds.CabinetOtaStatus, Protocol.CmdCabinetOtaStatus)]
    [InlineData(CmdIds.CabinetOtaResponse, Protocol.CmdCabinetOtaResponse)]
    [InlineData(CmdIds.CabinetOtaProgress, Protocol.CmdCabinetOtaProgress)]
    [InlineData(CmdIds.CabinetOtaNodes, Protocol.CmdCabinetOtaNodes)]
    [InlineData(CmdIds.CabinetOtaNodesResponse, Protocol.CmdCabinetOtaNodesResponse)]
    public void OtaCommandMappings_RoundTrip(ushort id, string name)
    {
        Assert.Equal(name, CmdIds.ToCmdName(id));
        Assert.Equal(id, CmdIds.ToCmdId(name));
        Assert.NotNull(Protocol.ToCommandType(name));
    }

    [Fact]
    public void UploadChunk_FitsRootApplicationPayloadLimit()
    {
        string chunk = Convert.ToBase64String(
            new byte[CabinetOtaService.UploadChunkSize]);
        Message request = Message.Create(Protocol.CmdCabinetOtaChunk,
            "ROOT_B81F3FA9F404", new
            {
                upload_id = new string('a', 32),
                offset = 3_000_000,
                chunk_base64 = chunk
            });

        AppMessage app = AppMessageMapper.ToApp(request);

        Assert.True(CabinetOtaService.UploadChunkSize <= 3072);
        Assert.True(app.Payload.Length <= 4000,
            $"OTA JSON payload is {app.Payload.Length} bytes");
        Assert.NotNull(FrameCodec.Encode(BinaryMessageCodec.Encode(app)));
    }

    [Fact]
    public void MeshLiteOta_RootAndCabinetBuildVersionsDiffer()
    {
        string rootCmake = File.ReadAllText(FindRepositoryFile(
            Path.Combine("esp32", "root_node", "CMakeLists.txt")));
        string cabinetCmake = File.ReadAllText(FindRepositoryFile(
            Path.Combine("esp32", "cabinet_node", "CMakeLists.txt")));

        string rootVersion = ParseProjectVersion(rootCmake);
        string cabinetVersion = ParseProjectVersion(cabinetCmake);

        Assert.NotEqual(cabinetVersion, rootVersion);
        Assert.EndsWith("-root", rootVersion, StringComparison.Ordinal);
        Assert.EndsWith("-cab", cabinetVersion, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseNodesResponse_ReadsTopologyAndProgressFields()
    {
        var response = new Message
        {
            Cmd = Protocol.CmdCabinetOtaNodesResponse,
            Data = JObject.Parse("""
                {
                  "offset": 10,
                  "count": 1,
                  "total": 40,
                  "nodes": [
                    {
                      "device_id": "CAB_AABBCCDDEEFF",
                      "parent_device_id": "CAB_112233445566",
                      "version": "0.0.1-cab",
                      "phase": "downloading",
                      "error": "",
                      "mesh_layer": 3,
                      "progress": 47,
                      "retry_count": 2,
                      "updated_ago": 4,
                      "online": true,
                      "compatible": true
                    }
                  ]
                }
                """)
        };

        CabinetOtaNodePage page = CabinetOtaService.ParseNodesResponse(response);

        Assert.Equal(10, page.Offset);
        Assert.Equal(40, page.Total);
        CabinetOtaNodeStatus node = Assert.Single(page.Nodes);
        Assert.Equal("CAB_AABBCCDDEEFF", node.DeviceId);
        Assert.Equal("CAB_112233445566", node.ParentDeviceId);
        Assert.Equal("0.0.1-cab", node.Version);
        Assert.Equal("downloading", node.Phase);
        Assert.Equal(3, node.MeshLayer);
        Assert.Equal(47, node.Progress);
        Assert.Equal(2, node.RetryCount);
        Assert.Equal(4U, node.UpdatedAgoSeconds);
        Assert.True(node.Online);
        Assert.True(node.Compatible);
    }

    [Fact]
    public void OtaProgressBar_BindsReadOnlyProgressOneWay()
    {
        string xaml = File.ReadAllText(FindRepositoryFile(
            Path.Combine("CabinetLock", "CabinetLock", "Views",
                "CabinetOtaWindow.xaml")));

        Assert.Contains("Value=\"{Binding Progress, Mode=OneWay}\"", xaml,
            StringComparison.Ordinal);
    }

    [Fact]
    public void OtaWindow_ShowsRootReportedDistributionElapsedTime()
    {
        string xaml = File.ReadAllText(FindRepositoryFile(
            Path.Combine("CabinetLock", "CabinetLock", "Views",
                "CabinetOtaWindow.xaml")));
        string code = File.ReadAllText(FindRepositoryFile(
            Path.Combine("CabinetLock", "CabinetLock", "Views",
                "CabinetOtaWindow.xaml.cs")));

        Assert.Contains("x:Name=\"ElapsedText\"", xaml,
            StringComparison.Ordinal);
        Assert.Contains("分发用时", xaml, StringComparison.Ordinal);
        Assert.Contains("status.ElapsedSeconds", code,
            StringComparison.Ordinal);
        Assert.Contains("FormatDuration", code, StringComparison.Ordinal);
        Assert.Contains("正在拓扑下载", code, StringComparison.Ordinal);
    }

    [Fact]
    public void OtaStatus_CarriesDistributionTimingFields()
    {
        var status = new CabinetOtaStatus
        {
            StartedAtSeconds = 12,
            ElapsedSeconds = 345
        };

        Assert.Equal(12U, status.StartedAtSeconds);
        Assert.Equal(345U, status.ElapsedSeconds);
    }

    [Fact]
    public void Deploy_CanReuseOnlyAnExactlyMatchingValidatedRootImage()
    {
        var firmware = new CabinetFirmwareInfo
        {
            Version = "3.2.0-idf",
            Sha256 = new string('a', 64),
            ImageSize = 957_696
        };
        var matching = new CabinetOtaStatus
        {
            Phase = "ready",
            Version = firmware.Version,
            Sha256 = firmware.Sha256.ToUpperInvariant(),
            ImageSize = (uint)firmware.ImageSize,
            ReceivedBytes = (uint)firmware.ImageSize
        };

        Assert.True(CabinetOtaService.CanReuseStagedImage(matching, firmware));
        Assert.False(CabinetOtaService.CanReuseStagedImage(
            new CabinetOtaStatus
            {
                Phase = "ready",
                Version = firmware.Version,
                Sha256 = new string('b', 64),
                ImageSize = (uint)firmware.ImageSize,
                ReceivedBytes = (uint)firmware.ImageSize
            }, firmware));
    }

    private static byte[] CreateImage(string projectName, string version)
    {
        byte[] image = new byte[128 * 1024];
        image[0] = 0xE9;
        image[12] = 0x09;
        image[13] = 0x00;
        image[32] = 0x32;
        image[33] = 0x54;
        image[34] = 0xCD;
        image[35] = 0xAB;
        Encoding.ASCII.GetBytes(version).CopyTo(image, 48);
        Encoding.ASCII.GetBytes(projectName).CopyTo(image, 80);
        return image;
    }

    private static string ParseProjectVersion(string cmake)
    {
        Match match = Regex.Match(cmake,
            "set\\(PROJECT_VER\\s+\"(?<version>[^\"]+)\"\\)");
        Assert.True(match.Success, "PROJECT_VER was not found in CMakeLists.txt");
        return match.Groups["version"].Value;
    }

    private static string FindRepositoryFile(string relativePath)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory != null)
        {
            string candidate = Path.Combine(directory.FullName, relativePath);
            if (File.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }

        throw new FileNotFoundException(
            $"Could not find repository file '{relativePath}' from '{AppContext.BaseDirectory}'.");
    }
}
