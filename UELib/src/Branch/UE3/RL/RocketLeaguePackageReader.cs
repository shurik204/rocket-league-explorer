using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using UELib.Services;

namespace UELib.Branch.UE3.RL
{
    /// <summary>
    /// Prepares Rocket League packages for UELib.
    ///
    /// Since licensee version 22 Rocket League packages store their header tables
    /// (names/imports/exports) AES-256-ECB encrypted and their object data in a
    /// series of zlib compressed chunks.  The public RLUPKTool only reconstructs
    /// part of that image for recent builds: it writes the decrypted header and
    /// the first decompressed chunk into a file sized like the original package,
    /// which leaves most export data beyond EOF.  Loading such a file makes every
    /// UClass look empty and the decompiler renders `class X dllbind()`.
    ///
    /// This reader rebuilds the complete logical package in memory:
    ///   * decrypt the header region with Rocket League's AES key,
    ///   * walk the physical chunk table at TotalHeaderSize,
    ///   * inflate every block and place the payload at DependsOffset.
    ///
    /// The result is written to a cache file and returned to UnrealLoader.  The
    /// cache file keeps the original file name so package/object paths are
    /// unchanged.
    /// </summary>
    internal static class RocketLeaguePackageReader
    {
        private const uint PackageSignature = 0x9E2A83C1;

        // Rocket League's package AES key (same key used by RLUPKTool/UModel).
        private static readonly byte[] AesKey =
        {
            0xC7, 0xDF, 0x6B, 0x13, 0x25, 0x2A, 0xCC, 0x71,
            0x47, 0xBB, 0x51, 0xC9, 0x8A, 0xD7, 0xE3, 0x4B,
            0x7F, 0xE5, 0x00, 0xB7, 0x7F, 0xA5, 0xFA, 0xB2,
            0x93, 0xE2, 0xF2, 0x4E, 0x6B, 0x17, 0xE7, 0x79
        };

        private sealed class SummaryInfo
        {
            public int TotalHeaderSize;
            public int NameOffset;
            public int ExportCount;
            public int ExportOffset;
            public int DependsOffset;
            public int CompressionFlagsOffset;
            public bool HeaderIsPlaintext;
            public bool HasChunkData;
            public long MaxExportEnd;
        }

        private sealed class BlockInfo
        {
            public int CompressedSize;
            public int UncompressedSize;
        }

        private sealed class ChunkInfo
        {
            public List<BlockInfo> Blocks = new();
            public long CompressedOffset;
            public int UncompressedSize;
        }

        /// <summary>
        /// Returns a path to a fully reconstructed package, or null when the file
        /// does not need (or cannot be given) reconstruction.
        /// </summary>
        public static string? PreparePackage(string packagePath)
        {
            try
            {
                if (string.IsNullOrEmpty(packagePath) || !File.Exists(packagePath))
                {
                    return null;
                }

                packagePath = Path.GetFullPath(packagePath);

                var sourcePath = packagePath;
                var info = ReadSummary(sourcePath);
                if (info == null)
                {
                    return null;
                }

                var fileLength = new FileInfo(sourcePath).Length;
                var isIncomplete = info.MaxExportEnd > fileLength;

                // A complete package either has all of its export data in the file
                // and no physical zlib chunks, or is already a valid image.
                if (!info.HasChunkData && !isIncomplete)
                {
                    return null;
                }

                // Files produced by RLUPKTool for recent builds are truncated:
                // the header is plaintext and the first decompressed chunk
                // overwrote the rest of the original layout.  The encrypted
                // original usually sits next to it as `<name>_.upk`.
                if (info.HeaderIsPlaintext && isIncomplete)
                {
                    var sibling = GetEncryptedSibling(packagePath);
                    if (sibling != null)
                    {
                        var siblingInfo = ReadSummary(sibling);
                        if (siblingInfo != null && siblingInfo.HasChunkData)
                        {
                            sourcePath = sibling;
                            info = siblingInfo;
                        }
                    }
                }

                if (!info.HasChunkData)
                {
                    Log($"Rocket League package '{Path.GetFileName(packagePath)}' is incomplete " +
                        "and no usable encrypted source was found next to it.");
                    return null;
                }

                var chunks = ReadChunks(sourcePath, info.TotalHeaderSize);
                if (chunks.Count == 0)
                {
                    return null;
                }

                long payloadSize = 0;
                foreach (var chunk in chunks)
                {
                    payloadSize += chunk.UncompressedSize;
                }

                var dataBase = info.DependsOffset > 0 && info.DependsOffset < info.TotalHeaderSize
                    ? info.DependsOffset
                    : info.TotalHeaderSize;
                var imageSize = dataBase + payloadSize;
                if (imageSize < info.MaxExportEnd)
                {
                    // Export offsets are authoritative; pad rather than truncate.
                    imageSize = info.MaxExportEnd;
                }

                var cachePath = GetCachePath(packagePath, sourcePath, imageSize);
                var cacheInfo = new FileInfo(cachePath);
                if (cacheInfo.Exists && cacheInfo.Length == imageSize)
                {
                    return cachePath;
                }

                var image = BuildImage(sourcePath, info, chunks, dataBase, imageSize);
                WriteCache(cachePath, image);
                Log($"Reconstructed Rocket League package '{Path.GetFileName(packagePath)}' " +
                    $"from '{Path.GetFileName(sourcePath)}' ({imageSize} bytes).");
                return cachePath;
            }
            catch (Exception exception)
            {
                LibServices.LogService.SilentException(
                    new UnrealException($"Failed to reconstruct Rocket League package '{packagePath}'", exception));
                return null;
            }
        }

        /// <summary>
        /// Prepares <paramref name="packagePath"/> and returns whether a
        /// reconstructed image should be loaded instead.
        /// </summary>
        public static bool TryPreparePackage(string packagePath, out string loadPath)
        {
            var prepared = PreparePackage(packagePath);
            if (prepared == null)
            {
                loadPath = packagePath;
                return false;
            }

            loadPath = prepared;
            return true;
        }

        private static SummaryInfo? ReadSummary(string path)
        {
            var fileInfo = new FileInfo(path);
            if (fileInfo.Length < 64)
            {
                return null;
            }

            using var file = File.OpenRead(path);
            var prefix = new byte[64];
            ReadExactly(file, prefix, 0, prefix.Length);
            if (BitConverter.ToUInt32(prefix, 0) != PackageSignature)
            {
                return null;
            }

            var version = BitConverter.ToUInt16(prefix, 4);
            var licenseeVersion = BitConverter.ToUInt16(prefix, 6);
            if (version < 867 || version > 868 || licenseeVersion < 22)
            {
                return null;
            }

            // The summary (names/imports/exports) lives entirely before
            // TotalHeaderSize.  Only read that much: complete rebuilt packages
            // can be hundreds of megabytes, and this method also runs for every
            // non-RL package opened by the loader.
            var totalHeaderSizePreview = BitConverter.ToInt32(prefix, 8);
            if (totalHeaderSizePreview <= 0 || totalHeaderSizePreview > fileInfo.Length)
            {
                return null;
            }

            var needed = Math.Min(fileInfo.Length, (long)totalHeaderSizePreview + 16);
            var data = new byte[(int)needed];
            file.Position = 0;
            ReadExactly(file, data, 0, data.Length);

            var offset = 8;
            var totalHeaderSize = ReadInt32(data, ref offset);
            var folderName = ReadString(data, ref offset);
            ReadUInt32(data, ref offset); // PackageFlags
            var nameCount = ReadInt32(data, ref offset);
            var nameOffset = ReadInt32(data, ref offset);
            var exportCount = ReadInt32(data, ref offset);
            var exportOffset = ReadInt32(data, ref offset);
            offset += 8; // ImportCount, ImportOffset
            var dependsOffset = ReadInt32(data, ref offset);

            // Four unknown offsets followed by the package GUID.
            offset += 16 + 16;
            var generationCount = ReadInt32(data, ref offset);
            if (generationCount < 0 || generationCount > 1024)
            {
                return null;
            }

            offset += generationCount * 12;
            var engineVersion = ReadUInt32(data, ref offset);
            // CookerVersion, CompressionFlags, CompressedChunks.Count.  The
            // chunk array is empty/stale in recent packages; reconstruction
            // walks the physical chunk table instead.
            var compressionFlagsOffset = offset + 4;
            offset += 12;

            if (nameCount <= 0 || nameOffset <= 0 || nameOffset >= data.Length ||
                exportCount <= 0 || exportOffset <= 0 || exportOffset >= data.Length ||
                totalHeaderSize <= 0 || totalHeaderSize > data.Length)
            {
                return null;
            }

            // Rocket League packages identify themselves with an empty group and
            // the engine build used by the current Live client.
            if (folderName != "None" || engineVersion < 10897)
            {
                return null;
            }

            var headerIsPlaintext = LooksLikeNameTable(data, nameOffset);

            var hasChunkData = totalHeaderSize + 16 <= data.Length &&
                               BitConverter.ToUInt32(data, totalHeaderSize) == PackageSignature &&
                               BitConverter.ToInt32(data, totalHeaderSize + 8) > 0 &&
                               BitConverter.ToInt32(data, totalHeaderSize + 12) > 0;

            var maxExportEnd = headerIsPlaintext ? FindMaxExportEnd(data, exportOffset, exportCount) : 0;

            return new SummaryInfo
            {
                TotalHeaderSize = totalHeaderSize,
                NameOffset = nameOffset,
                ExportCount = exportCount,
                ExportOffset = exportOffset,
                DependsOffset = dependsOffset,
                CompressionFlagsOffset = compressionFlagsOffset,
                HeaderIsPlaintext = headerIsPlaintext,
                HasChunkData = hasChunkData,
                MaxExportEnd = maxExportEnd
            };
        }

        private static bool LooksLikeNameTable(byte[] data, int nameOffset)
        {
            if (nameOffset < 0 || nameOffset + 8 > data.Length)
            {
                return false;
            }

            var length = BitConverter.ToInt32(data, nameOffset);
            if (length == 0)
            {
                return false;
            }

            var byteCount = length > 0 ? length : -length * 2;
            if (byteCount < 1 || byteCount > 4096 || nameOffset + 4 + byteCount > data.Length)
            {
                return false;
            }

            var start = nameOffset + 4;
            if (length > 0)
            {
                if (data[start + byteCount - 1] != 0)
                {
                    return false;
                }

                for (var i = 0; i < byteCount - 1; ++i)
                {
                    var b = data[start + i];
                    if (b != 0 && (b < 0x20 || b > 0x7E))
                    {
                        return false;
                    }
                }
            }
            else
            {
                if (data[start + byteCount - 1] != 0 || data[start + byteCount - 2] != 0)
                {
                    return false;
                }
            }

            return true;
        }

        private static long FindMaxExportEnd(byte[] data, int exportOffset, int exportCount)
        {
            long max = 0;
            var offset = exportOffset;
            try
            {
                for (var i = 0; i < exportCount; ++i)
                {
                    // ClassIndex, SuperIndex, OuterIndex, ObjectName, ArchetypeIndex.
                    offset += 12 + 8 + 4;
                    if (offset + 8 + 4 + 8 > data.Length)
                    {
                        return max;
                    }

                    // ObjectFlags is stored as two 32-bit halves in UE3 packages.
                    offset += 8;
                    var serialSize = BitConverter.ToInt32(data, offset);
                    var serialOffset = BitConverter.ToInt64(data, offset + 4);
                    offset += 4 + 8;

                    if (serialSize > 0 && serialOffset > 0)
                    {
                        max = Math.Max(max, serialOffset + serialSize);
                    }

                    var exportFlags = BitConverter.ToUInt32(data, offset);
                    var netObjectCount = BitConverter.ToInt32(data, offset + 4);
                    offset += 8;

                    if (netObjectCount < 0 || netObjectCount > 4096)
                    {
                        return max;
                    }

                    offset += netObjectCount * 4 + 16 + 4;
                    if (offset > data.Length)
                    {
                        return max;
                    }

                    _ = exportFlags;
                }
            }
            catch (ArgumentOutOfRangeException)
            {
                // A partially available export table is still enough to know the
                // package is truncated; the caller only compares against the file length.
            }

            return max;
        }

        private static List<ChunkInfo> ReadChunks(string path, int totalHeaderSize)
        {
            var data = File.ReadAllBytes(path);
            var chunks = new List<ChunkInfo>();
            var offset = totalHeaderSize;
            while (offset + 16 <= data.Length)
            {
                if (BitConverter.ToUInt32(data, offset) != PackageSignature)
                {
                    break;
                }

                var blockSize = BitConverter.ToUInt32(data, offset + 4);
                var compressedSize = BitConverter.ToInt32(data, offset + 8);
                var uncompressedSize = BitConverter.ToInt32(data, offset + 12);
                if (blockSize == 0 || blockSize > 0x400000)
                {
                    blockSize = 0x20000;
                }

                if (compressedSize <= 0 || uncompressedSize <= 0 ||
                    compressedSize > 64 * 1024 * 1024 || uncompressedSize > 256 * 1024 * 1024)
                {
                    break;
                }

                var blockCount = (uncompressedSize + (int)blockSize - 1) / (int)blockSize;
                offset += 16;
                if (blockCount <= 0 || blockCount > 8192 || offset + blockCount * 8 > data.Length)
                {
                    break;
                }

                var blocks = new List<BlockInfo>(blockCount);
                var compressedBytes = 0;
                for (var i = 0; i < blockCount; ++i)
                {
                    var block = new BlockInfo
                    {
                        CompressedSize = BitConverter.ToInt32(data, offset),
                        UncompressedSize = BitConverter.ToInt32(data, offset + 4)
                    };
                    offset += 8;
                    if (block.CompressedSize <= 0 || block.UncompressedSize <= 0)
                    {
                        return chunks;
                    }

                    compressedBytes += block.CompressedSize;
                    blocks.Add(block);
                }

                if (offset + compressedBytes > data.Length)
                {
                    break;
                }

                chunks.Add(new ChunkInfo
                {
                    CompressedOffset = offset,
                    UncompressedSize = uncompressedSize,
                    Blocks = blocks
                });
                offset += compressedBytes;
            }

            return chunks;
        }

        private static byte[] BuildImage(string sourcePath, SummaryInfo info, List<ChunkInfo> chunks,
            int dataBase, long imageSize)
        {
            var source = File.ReadAllBytes(sourcePath);
            if (imageSize > int.MaxValue)
            {
                throw new InvalidDataException("Reconstructed Rocket League package exceeds the 2 GB limit.");
            }

            var image = new byte[(int)imageSize];

            var headerLength = Math.Min(info.TotalHeaderSize, source.Length);
            Array.Copy(source, 0, image, 0, headerLength);

            if (!info.HeaderIsPlaintext)
            {
                DecryptHeader(image, source, info);
            }

            // The object data is already uncompressed in this image.  Clear the
            // summary compression flag so UELib does not bail out waiting for
            // an external decompressor when a package still lists chunks.
            if (info.CompressionFlagsOffset >= 0 && info.CompressionFlagsOffset + 4 <= image.Length)
            {
                image[info.CompressionFlagsOffset] = 0;
                image[info.CompressionFlagsOffset + 1] = 0;
                image[info.CompressionFlagsOffset + 2] = 0;
                image[info.CompressionFlagsOffset + 3] = 0;
            }

            var writeOffset = dataBase;
            foreach (var chunk in chunks)
            {
                var offset = (int)chunk.CompressedOffset;
                foreach (var block in chunk.Blocks)
                {
                    var blockData = Inflate(source, offset, block.CompressedSize, block.UncompressedSize);
                    Array.Copy(blockData, 0, image, writeOffset, blockData.Length);
                    writeOffset += blockData.Length;
                    offset += block.CompressedSize;
                }
            }

            return image;
        }

        private static void DecryptHeader(byte[] image, byte[] source, SummaryInfo info)
        {
            var encryptedLength = info.TotalHeaderSize - info.NameOffset;
            if (encryptedLength <= 0)
            {
                return;
            }

            // The encrypted region is padded to the AES block size.
            encryptedLength = (encryptedLength + 15) & ~15;
            if (encryptedLength <= 0)
            {
                return;
            }

            var encrypted = new byte[encryptedLength];
            var available = Math.Min(encryptedLength, source.Length - info.NameOffset);
            if (available > 0)
            {
                Array.Copy(source, info.NameOffset, encrypted, 0, available);
            }

            using var aes = Aes.Create();
            aes.KeySize = 256;
            aes.Mode = CipherMode.ECB;
            aes.Padding = PaddingMode.None;
            aes.Key = AesKey;
            using var decryptor = aes.CreateDecryptor();
            var decrypted = decryptor.TransformFinalBlock(encrypted, 0, encrypted.Length);
            Array.Copy(decrypted, 0, image, info.NameOffset,
                Math.Min(decrypted.Length, image.Length - info.NameOffset));
        }

        private static byte[] Inflate(byte[] source, int offset, int compressedSize, int uncompressedSize)
        {
            // The cooked chunks use a zlib wrapper (0x78 ..); DeflateStream wants
            // the raw DEFLATE stream, so skip the two-byte zlib header.
            var start = offset;
            var count = compressedSize;
            if (count >= 2 && (source[offset] & 0x0F) == 8)
            {
                start += 2;
                count -= 2;
            }

            using var input = new MemoryStream(source, start, count, false);
            using var inflater = new DeflateStream(input, CompressionMode.Decompress);
            var result = new byte[uncompressedSize];
            var read = 0;
            while (read < result.Length)
            {
                var bytesRead = inflater.Read(result, read, result.Length - read);
                if (bytesRead <= 0)
                {
                    break;
                }

                read += bytesRead;
            }

            if (read != result.Length)
            {
                throw new InvalidDataException(
                    $"Could not inflate Rocket League chunk block (expected {uncompressedSize} bytes, got {read}).");
            }

            return result;
        }

        private static string? GetEncryptedSibling(string packagePath)
        {
            var directory = Path.GetDirectoryName(packagePath);
            var name = Path.GetFileNameWithoutExtension(packagePath);
            var extension = Path.GetExtension(packagePath);
            if (string.IsNullOrEmpty(directory) || string.IsNullOrEmpty(extension))
            {
                return null;
            }

            var candidates = name.EndsWith("_decrypted", StringComparison.Ordinal)
                ? new[]
                {
                    Path.Combine(directory, name.Substring(0, name.Length - "_decrypted".Length) + extension)
                }
                : name.EndsWith("_", StringComparison.Ordinal)
                    ? new[] { Path.Combine(directory, name.Substring(0, name.Length - 1) + extension) }
                    : new[]
                    {
                        Path.Combine(directory, name + "_" + extension),
                        Path.Combine(directory, name + "_decrypted" + extension)
                    };

            foreach (var candidate in candidates)
            {
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }

            return null;
        }

        private static string GetCachePath(string requestedPath, string sourcePath, long imageSize)
        {
            var root = Path.Combine(Path.GetTempPath(), "ue-explorer", "rl-packages");
            var stamp = $"{Path.GetFullPath(sourcePath)}|{new FileInfo(sourcePath).Length}|" +
                        $"{new FileInfo(sourcePath).LastWriteTimeUtc.Ticks}|{imageSize}";
            using var sha = SHA256.Create();
            var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(stamp));
            var key = BitConverter.ToString(hash, 0, 8).Replace("-", "").ToLowerInvariant();
            var directory = Path.Combine(root, key);
            Directory.CreateDirectory(directory);
            return Path.Combine(directory, Path.GetFileName(requestedPath));
        }

        private static void WriteCache(string path, byte[] image)
        {
            var tempPath = path + ".tmp";
            File.WriteAllBytes(tempPath, image);
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            File.Move(tempPath, path);
        }

        private static void ReadExactly(Stream stream, byte[] buffer, int offset, int count)
        {
            while (count > 0)
            {
                var read = stream.Read(buffer, offset, count);
                if (read <= 0)
                {
                    throw new EndOfStreamException();
                }

                offset += read;
                count -= read;
            }
        }

        private static int ReadInt32(byte[] data, ref int offset)
        {
            var value = BitConverter.ToInt32(data, offset);
            offset += 4;
            return value;
        }

        private static uint ReadUInt32(byte[] data, ref int offset)
        {
            var value = BitConverter.ToUInt32(data, offset);
            offset += 4;
            return value;
        }

        private static string ReadString(byte[] data, ref int offset)
        {
            var length = ReadInt32(data, ref offset);
            if (length == 0)
            {
                return string.Empty;
            }

            if (length > 0)
            {
                var value = Encoding.ASCII.GetString(data, offset, length - 1);
                offset += length;
                return value;
            }

            var byteCount = -length * 2;
            var wideValue = Encoding.Unicode.GetString(data, offset, byteCount - 2);
            offset += byteCount;
            return wideValue;
        }

        private static void Log(string message)
        {
            LibServices.LogService.Log(message);
        }
    }
}
