using System;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;

namespace JackOnTheRocks
{
    /// <summary>
    /// Simulated server that 'signs' receipts and can verify signatures.
    /// Replace with a real server implementation for production.
    /// </summary>
    public static class ServerStub
    {
        private static readonly string serverSecret = "server_demo_secret";

        /// <summary>
        /// Simulate receiving a client payload and return a server signature.
        /// </summary>
        public static string ReceiveAndSign(string payload, string clientSignature)
        {
            // In a real server, you'd validate clientSignature and possibly user auth.
            // Here we ignore clientSignature and sign the payload with serverSecret.
            return ComputeHMAC(payload, serverSecret);
        }

        public static bool VerifySignature(string payload, string serverSignature)
        {
            var expected = ComputeHMAC(payload, serverSecret);
            return expected == serverSignature;
        }

        private static string ComputeHMAC(string message, string key)
        {
            using (var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(key)))
            {
                var bytes = Encoding.UTF8.GetBytes(message);
                var hash = hmac.ComputeHash(bytes);
                return Convert.ToBase64String(hash);
            }
        }
    }
}
