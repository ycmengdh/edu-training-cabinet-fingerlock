using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CabinetLock
{
    public sealed class BusinessSnapshot
    {
        internal BusinessSnapshot(
            byte[] header, byte[] compressedPayload, byte[] rawPayload,
            uint generation, uint createdUnix, byte[] uploadId,
            byte[] contentSha256, byte[] compressedSha256)
        {
            Header = header;
            CompressedPayload = compressedPayload;
            RawPayload = rawPayload;
            Generation = generation;
            CreatedUnix = createdUnix;
            UploadId = uploadId;
            ContentSha256 = contentSha256;
            CompressedSha256 = compressedSha256;
        }

        public byte[] Header { get; }
        public byte[] CompressedPayload { get; }
        public byte[] RawPayload { get; }
        public uint Generation { get; }
        public uint CreatedUnix { get; }
        public byte[] UploadId { get; }
        public byte[] ContentSha256 { get; }
        public byte[] CompressedSha256 { get; }
        public int ContainerSize => BusinessSnapshotCodec.HeaderSize + CompressedPayload.Length;

        public byte[] ToContainerBytes()
        {
            byte[] output = new byte[ContainerSize];
            Buffer.BlockCopy(Header, 0, output, 0, Header.Length);
            Buffer.BlockCopy(CompressedPayload, 0, output, Header.Length,
                CompressedPayload.Length);
            return output;
        }
    }

    public sealed class BusinessSnapshotPackage
    {
        public Dictionary<string, JArray> Tables { get; } =
            new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, uint> Versions { get; } =
            new(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Host-side codec for the opaque SD business snapshot. The root node only
    /// validates this fixed header and the compressed SHA-256; it never parses
    /// the JSON or Gzip payload.
    /// </summary>
    public static class BusinessSnapshotCodec
    {
        public const int HeaderSize = 108;
        public const byte FormatVersion = 1;
        public const byte GzipEncoding = 1;
        public const int MaxCompressedSize = 32 * 1024 * 1024;
        public const int MaxRawSize = 128 * 1024 * 1024;

        private static readonly byte[] Magic = Encoding.ASCII.GetBytes("BSNP");

        public static BusinessSnapshot CreateFromDatabase(uint? generation = null)
        {
            BusinessDatabase.Initialize();
            var tables = new Dictionary<string, JArray>(StringComparer.OrdinalIgnoreCase);
            var versions = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
            foreach (string table in BusinessDatabase.DailySyncTables)
            {
                tables[table] = BusinessDatabase.ReadArray(table) ?? new JArray();
                versions[table] = BusinessDatabase.GetTableVersion(table);
            }
            return Create(tables, versions, generation);
        }

        public static BusinessSnapshot Create(
            IReadOnlyDictionary<string, JArray> tables,
            IReadOnlyDictionary<string, uint>? versions = null,
            uint? generation = null)
        {
            if (tables == null) throw new ArgumentNullException(nameof(tables));

            var tableObject = new JObject();
            var versionObject = new JObject();
            foreach (string table in BusinessDatabase.DailySyncTables)
            {
                tableObject[table] = tables.TryGetValue(table, out JArray? value)
                    ? value.DeepClone()
                    : new JArray();
                versionObject[table] = versions != null &&
                    versions.TryGetValue(table, out uint version) ? version : 0;
            }

            var package = new JObject
            {
                ["format_version"] = FormatVersion,
                ["tables"] = tableObject,
                ["versions"] = versionObject
            };
            byte[] raw = Encoding.UTF8.GetBytes(package.ToString(Formatting.None));
            if (raw.Length > MaxRawSize)
                throw new InvalidDataException("Business snapshot is too large");

            byte[] compressed;
            using (var output = new MemoryStream())
            {
                using (var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true))
                    gzip.Write(raw, 0, raw.Length);
                compressed = output.ToArray();
            }
            if (compressed.Length > MaxCompressedSize)
                throw new InvalidDataException("Compressed business snapshot is too large");

            byte[] contentHash = SHA256.HashData(raw);
            byte[] compressedHash = SHA256.HashData(compressed);
            byte[] uploadId = contentHash.AsSpan(0, 16).ToArray();
            uint createdUnix = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            uint snapshotGeneration = generation ?? createdUnix;
            byte[] header = BuildHeader(snapshotGeneration, createdUnix, raw.Length,
                compressed.Length, uploadId, contentHash, compressedHash);
            return new BusinessSnapshot(header, compressed, raw, snapshotGeneration,
                createdUnix, uploadId, contentHash, compressedHash);
        }

        public static BusinessSnapshot Decode(byte[] container)
        {
            if (container == null || container.Length < HeaderSize)
                throw new InvalidDataException("Business snapshot header is missing");

            ReadOnlySpan<byte> header = container.AsSpan(0, HeaderSize);
            if (!header.Slice(0, 4).SequenceEqual(Magic) ||
                header[4] != FormatVersion || header[5] != GzipEncoding ||
                BinaryPrimitives.ReadUInt16LittleEndian(header.Slice(6, 2)) != HeaderSize)
                throw new InvalidDataException("Business snapshot header is invalid");

            uint generation = BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(8, 4));
            uint compressedSize = BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(12, 4));
            uint rawSize = BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(16, 4));
            uint createdUnix = BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(20, 4));
            if (compressedSize > MaxCompressedSize || rawSize > MaxRawSize ||
                container.Length != HeaderSize + compressedSize)
                throw new InvalidDataException("Business snapshot length is invalid");

            byte[] uploadId = header.Slice(24, 16).ToArray();
            byte[] contentHash = header.Slice(40, 32).ToArray();
            byte[] compressedHash = header.Slice(72, 32).ToArray();
            byte[] compressed = container.AsSpan(HeaderSize, (int)compressedSize).ToArray();
            if (!CryptographicOperations.FixedTimeEquals(
                    SHA256.HashData(compressed), compressedHash))
                throw new InvalidDataException("Compressed business snapshot SHA-256 mismatch");

            byte[] raw;
            using (var input = new MemoryStream(compressed, writable: false))
            using (var gzip = new GZipStream(input, CompressionMode.Decompress))
            using (var output = new MemoryStream((int)rawSize))
            {
                gzip.CopyTo(output);
                raw = output.ToArray();
            }
            if (raw.Length != rawSize || !CryptographicOperations.FixedTimeEquals(
                    SHA256.HashData(raw), contentHash))
                throw new InvalidDataException("Business snapshot content SHA-256 mismatch");

            return new BusinessSnapshot(header.ToArray(), compressed, raw, generation,
                createdUnix, uploadId, contentHash, compressedHash);
        }

        public static BusinessSnapshotPackage ReadPackage(BusinessSnapshot snapshot)
        {
            if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));
            JObject root;
            try
            {
                root = JObject.Parse(Encoding.UTF8.GetString(snapshot.RawPayload));
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException("Business snapshot JSON is invalid", ex);
            }
            if (root.Value<int?>("format_version") != FormatVersion ||
                root["tables"] is not JObject tables)
                throw new InvalidDataException("Business snapshot package is invalid");

            var result = new BusinessSnapshotPackage();
            JObject? versions = root["versions"] as JObject;
            foreach (string table in BusinessDatabase.DailySyncTables)
            {
                if (tables[table] is not JArray rows)
                    throw new InvalidDataException($"Business snapshot table is missing: {table}");
                result.Tables[table] = (JArray)rows.DeepClone();
                result.Versions[table] = versions?[table]?.Value<uint>() ?? 0;
            }
            return result;
        }

        public static bool TryReadHeader(byte[]? header, out uint compressedSize,
            out uint rawSize, out byte[] contentSha256)
        {
            compressedSize = rawSize = 0;
            contentSha256 = Array.Empty<byte>();
            if (header == null || header.Length != HeaderSize ||
                !header.AsSpan(0, 4).SequenceEqual(Magic) ||
                header[4] != FormatVersion || header[5] != GzipEncoding ||
                BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(6, 2)) != HeaderSize)
                return false;
            compressedSize = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(12, 4));
            rawSize = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(16, 4));
            contentSha256 = header.AsSpan(40, 32).ToArray();
            return compressedSize <= MaxCompressedSize && rawSize <= MaxRawSize;
        }

        private static byte[] BuildHeader(uint generation, uint createdUnix,
            int rawSize, int compressedSize, byte[] uploadId,
            byte[] contentHash, byte[] compressedHash)
        {
            byte[] header = new byte[HeaderSize];
            Magic.CopyTo(header, 0);
            header[4] = FormatVersion;
            header[5] = GzipEncoding;
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(6, 2), HeaderSize);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8, 4), generation);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(12, 4), (uint)compressedSize);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(16, 4), (uint)rawSize);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(20, 4), createdUnix);
            uploadId.CopyTo(header, 24);
            contentHash.CopyTo(header, 40);
            compressedHash.CopyTo(header, 72);
            return header;
        }
    }
}
