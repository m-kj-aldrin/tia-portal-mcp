using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using TiaOpennessMcpServer.Models;

namespace TiaOpennessMcpServer.Utilities;

public static class V1Checksums
{
    public const string Algorithm = "sha-256";
    public const string ContentEncoding = "utf-8-no-bom";
    public const string SimaticSdBundleScheme = "simatic-sd-bundle-v1";

    private static readonly Encoding Utf8NoBom = new UTF8Encoding(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    public static V1Document CreateDocument(
        string fileName,
        string content,
        string? role = null,
        string? mediaType = null)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            throw new ArgumentException("A document filename is required.", nameof(fileName));
        if (content is null)
            throw new ArgumentNullException(nameof(content));

        var checksum = ForContent(content);
        return new V1Document
        {
            FileName = fileName,
            Role = role,
            MediaType = mediaType,
            Content = content,
            ByteLength = checksum.ByteLength,
            Checksum = checksum,
        };
    }

    public static V1Checksum ForContent(string content)
    {
        if (content is null)
            throw new ArgumentNullException(nameof(content));

        var bytes = Utf8NoBom.GetBytes(content);
        return new V1Checksum
        {
            Algorithm = Algorithm,
            Encoding = ContentEncoding,
            Value = ComputeHash(bytes),
            ByteLength = bytes.LongLength,
        };
    }

    public static V1Checksum ForSimaticSdBundle(IEnumerable<V1Document> documents)
    {
        if (documents is null)
            throw new ArgumentNullException(nameof(documents));

        var ordered = documents
            .OrderBy(document => document?.FileName, StringComparer.Ordinal)
            .ToArray();

        if (ordered.Length == 0)
            throw new ArgumentException("A SIMATIC SD bundle must contain at least one document.", nameof(documents));
        if (ordered.Any(document => document is null))
            throw new ArgumentException("A SIMATIC SD bundle cannot contain a null document.", nameof(documents));

        var duplicateFileName = ordered
            .GroupBy(document => document.FileName, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateFileName is not null)
        {
            throw new ArgumentException(
                $"A SIMATIC SD bundle cannot contain duplicate filename '{duplicateFileName.Key}'.",
                nameof(documents));
        }

        using var sha256 = SHA256.Create();
        long aggregateByteLength = 0;

        foreach (var document in ordered)
        {
            if (string.IsNullOrEmpty(document.FileName))
                throw new ArgumentException("Every SIMATIC SD document must have a filename.", nameof(documents));
            if (document.Content is null)
                throw new ArgumentException($"Document '{document.FileName}' has null content.", nameof(documents));

            var fileNameBytes = Utf8NoBom.GetBytes(document.FileName);
            var contentBytes = Utf8NoBom.GetBytes(document.Content);
            var fileNameLength = UInt32BigEndian(checked((uint)fileNameBytes.LongLength));
            var contentLength = UInt64BigEndian(checked((ulong)contentBytes.LongLength));

            AddToHash(sha256, fileNameLength);
            AddToHash(sha256, fileNameBytes);
            AddToHash(sha256, contentLength);
            AddToHash(sha256, contentBytes);

            aggregateByteLength = checked(
                aggregateByteLength +
                fileNameLength.LongLength +
                fileNameBytes.LongLength +
                contentLength.LongLength +
                contentBytes.LongLength);
        }

        sha256.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return new V1Checksum
        {
            Algorithm = Algorithm,
            Encoding = ContentEncoding,
            Scheme = SimaticSdBundleScheme,
            Value = ToLowerHex(sha256.Hash ?? throw new CryptographicException("SHA-256 did not produce a hash.")),
            ByteLength = aggregateByteLength,
        };
    }

    public static long GetUtf8ByteLength(string value)
    {
        if (value is null)
            throw new ArgumentNullException(nameof(value));
        return Utf8NoBom.GetByteCount(value);
    }

    private static string ComputeHash(byte[] bytes)
    {
        using var sha256 = SHA256.Create();
        return ToLowerHex(sha256.ComputeHash(bytes));
    }

    private static void AddToHash(HashAlgorithm hash, byte[] bytes)
    {
        if (bytes.Length == 0)
            return;
        hash.TransformBlock(bytes, 0, bytes.Length, bytes, 0);
    }

    private static byte[] UInt32BigEndian(uint value) => new[]
    {
        (byte)(value >> 24),
        (byte)(value >> 16),
        (byte)(value >> 8),
        (byte)value,
    };

    private static byte[] UInt64BigEndian(ulong value) => new[]
    {
        (byte)(value >> 56),
        (byte)(value >> 48),
        (byte)(value >> 40),
        (byte)(value >> 32),
        (byte)(value >> 24),
        (byte)(value >> 16),
        (byte)(value >> 8),
        (byte)value,
    };

    private static string ToLowerHex(byte[] bytes)
    {
        const string hex = "0123456789abcdef";
        var result = new char[checked(bytes.Length * 2)];
        for (var index = 0; index < bytes.Length; index++)
        {
            result[index * 2] = hex[bytes[index] >> 4];
            result[index * 2 + 1] = hex[bytes[index] & 0x0f];
        }
        return new string(result);
    }
}
