using System;
using System.Security.Cryptography;
using System.Text;

namespace Pulse
{
    internal static class DeterministicSessionGuid
    {
        private const string CanonicalPrefix = "playlog:game-activity-import:v1:";

        public static Guid FromGaActivity(
            string playniteId,
            DateTime startUtc,
            DateTime endUtc,
            int itemOrdinal)
        {
            var canonical =
                CanonicalPrefix +
                playniteId + "|" +
                startUtc.ToUniversalTime().Ticks.ToString("d") + "|" +
                endUtc.ToUniversalTime().Ticks.ToString("d") + "|" +
                itemOrdinal.ToString("d");

            using (var sha = SHA256.Create())
            {
                var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(canonical));
                var bytes = new byte[16];
                Buffer.BlockCopy(hash, 0, bytes, 0, 16);
                bytes[6] = (byte)((bytes[6] & 0x0F) | 0x40);
                bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
                return new Guid(bytes);
            }
        }
    }
}
