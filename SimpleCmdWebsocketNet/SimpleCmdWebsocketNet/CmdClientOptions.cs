using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;

namespace SimpleCmdWebsocketNet
{
    public class CmdClientOptions
    {
        public string Url { get; set; }

        public bool ValidateCertificate => !string.IsNullOrEmpty(Url) && Url.StartsWith("wss:", StringComparison.OrdinalIgnoreCase);

        public bool AddOrigin { get; set; } = true;

        public string Origin
        {
            get 
            {
                if (!AddOrigin)
                    return "";

                var uri = new Uri(Url);
                return $"{(ValidateCertificate ? "https" : "http")}://{uri.Host}";
            }
        }

        public bool EnableConnectTimeout { get; set; } = true;

        public int ConnectTimeoutSeconds { get; set; } = 10;

        public Func<byte[], MessageParseResult> MessageParser { get; set; }

    }
}
