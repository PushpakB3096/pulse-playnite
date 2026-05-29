using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Playnite.SDK;
using Playnite.SDK.Models;

namespace Pulse
{
    public sealed class PlayniteCoverMetadata
    {
        public string SourceKind { get; set; }
        public string Hash { get; set; }
        public long ByteSize { get; set; }
        public string ContentType { get; set; }
        public string Url { get; set; }
        public string FilePath { get; set; }
    }

    public static class PlayniteCoverReader
    {
        public static PlayniteCoverMetadata TryRead(IPlayniteAPI api, Game game)
        {
            if (api == null || game == null)
            {
                return null;
            }

            var coverImage = game.CoverImage;
            if (string.IsNullOrWhiteSpace(coverImage))
            {
                return null;
            }

            var trimmed = coverImage.Trim();
            if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
                && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            {
                return new PlayniteCoverMetadata
                {
                    SourceKind = "url",
                    Url = trimmed
                };
            }

            string fullPath = null;
            try
            {
                fullPath = api.Database.GetFullFilePath(trimmed);
            }
            catch
            {
                return null;
            }

            if (string.IsNullOrWhiteSpace(fullPath) || !File.Exists(fullPath))
            {
                return null;
            }

            byte[] bytes;
            try
            {
                bytes = File.ReadAllBytes(fullPath);
            }
            catch
            {
                return null;
            }

            if (bytes.Length == 0)
            {
                return null;
            }

            var hash = ComputeSha256Hex(bytes);
            return new PlayniteCoverMetadata
            {
                SourceKind = "file",
                Hash = hash,
                ByteSize = bytes.LongLength,
                ContentType = GuessContentType(fullPath),
                FilePath = fullPath
            };
        }

        private static string ComputeSha256Hex(byte[] bytes)
        {
            using (var sha = SHA256.Create())
            {
                var hashBytes = sha.ComputeHash(bytes);
                return string.Concat(hashBytes.Select(b => b.ToString("x2")));
            }
        }

        private static string GuessContentType(string path)
        {
            var ext = Path.GetExtension(path)?.Trim().ToLowerInvariant();
            switch (ext)
            {
                case ".png":
                    return "image/png";
                case ".gif":
                    return "image/gif";
                case ".webp":
                    return "image/webp";
                default:
                    return "image/jpeg";
            }
        }
    }
}
