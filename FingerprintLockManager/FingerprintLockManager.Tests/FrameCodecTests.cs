namespace FingerprintLockManager.Tests;

public class FrameCodecTests
{
    [Fact]
    public void EncodeDecode_RoundTripsNormalFrame()
    {
        const string json = "{\"cmd\":\"HEARTBEAT\",\"msg_id\":\"test-1\"}";

        byte[] frame = Assert.IsType<byte[]>(FrameCodec.Encode(json));

        Assert.Equal(json, FrameCodec.Decode(frame));
    }

    [Fact]
    public void StreamDecoder_ReassemblesFragmentedMessageAcrossArbitraryChunks()
    {
        string json = "{\"data\":\"" + new string('x', 8_000) + "\"}";
        byte[] encoded = Assert.IsType<byte[]>(FrameCodec.Encode(json));
        var decoder = new FrameStreamDecoder();
        var messages = new List<string>();

        int offset = 0;
        int chunkSize = 1;
        while (offset < encoded.Length)
        {
            int count = Math.Min(chunkSize, encoded.Length - offset);
            decoder.Append(encoded, offset, count, messages.Add);
            offset += count;
            chunkSize = chunkSize % 37 + 1;
        }

        Assert.Equal([json], messages);
    }

    [Fact]
    public void StreamDecoder_SkipsCorruptFrameAndContinues()
    {
        byte[] corrupt = Assert.IsType<byte[]>(FrameCodec.Encode("{\"cmd\":\"BAD\"}"));
        corrupt[^1] ^= 0xFF;
        const string expected = "{\"cmd\":\"GOOD\"}";
        byte[] good = Assert.IsType<byte[]>(FrameCodec.Encode(expected));
        byte[] input = new byte[corrupt.Length + good.Length];
        Buffer.BlockCopy(corrupt, 0, input, 0, corrupt.Length);
        Buffer.BlockCopy(good, 0, input, corrupt.Length, good.Length);
        var messages = new List<string>();

        new FrameStreamDecoder().Append(input, 0, input.Length, messages.Add);

        Assert.Equal([expected], messages);
    }

    [Fact]
    public void StreamDecoder_ReportsBootTextAndStillDecodesFollowingFrame()
    {
        byte[] bootText = System.Text.Encoding.ASCII.GetBytes(
            "ESP-ROM:esp32s3-20210327\r\nsdmmc_card_init failed\r\n");
        const string expected = "{\"cmd\":\"REGISTER\",\"device_id\":\"ROOT_001\"}";
        byte[] frame = Assert.IsType<byte[]>(FrameCodec.Encode(expected));
        byte[] input = new byte[bootText.Length + frame.Length];
        Buffer.BlockCopy(bootText, 0, input, 0, bootText.Length);
        Buffer.BlockCopy(frame, 0, input, bootText.Length, frame.Length);
        var messages = new List<string>();
        var unframed = new List<byte[]>();

        new FrameStreamDecoder().Append(input, 0, input.Length, messages.Add, unframed.Add);

        Assert.Equal([expected], messages);
        Assert.Equal(bootText, Assert.Single(unframed));
    }
}
