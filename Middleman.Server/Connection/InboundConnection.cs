﻿using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Middleman.Server.Request;
using Middleman.Server.Response;
using Middleman.Server.Utils;
using NLog;

namespace Middleman.Server.Connection
{
    public class InboundConnection : MiddlemanConnection
    {
        private static readonly Logger Log = LogManager.GetCurrentClassLogger();
        private static long _connectionCounter;
        protected static readonly Encoding HeaderEncoding = Encoding.ASCII;//Encoding.GetEncoding("us-ascii");
        protected TcpClient Connection;
        public long ConnectionId;
        protected NetworkStream NetworkStream;
        public bool MustClose = false;

        public InboundConnection(TcpClient connection)
        {
            Log.Debug("Constructing new inbound connection from [{0}] to [{1}].", connection.Client.RemoteEndPoint, connection.Client.LocalEndPoint);

            Connection = connection;
            NetworkStream = connection.GetStream();
            ConnectionId = Interlocked.Increment(ref _connectionCounter);
            RemoteEndPoint = (IPEndPoint)connection.Client.RemoteEndPoint;
        }

        //public bool DataAvailable
        //{
        //    get { return NetworkStream != null ? NetworkStream.DataAvailable : false; }
        //}

        public override bool IsSecure
        {
            get { return false; }
        }

        public bool IsConnected
        {
            get
            {
                if (!Connection.Connected)
                    return false;

                try
                {
                    return !(Connection.Client.Poll(100, SelectMode.SelectRead) && Connection.Client.Available == 0);
                }
                catch (SocketException)
                {
                    return false;
                }
            }
        }

        public IPEndPoint RemoteEndPoint { get; private set; }

        public virtual Task OpenAsync()
        {
            return OpenAsync(CancellationToken.None);
        }

        public virtual Task OpenAsync(CancellationToken ct)
        {
            return Task.FromResult(default(VoidTypeStruct));
        }

        protected virtual Stream GetReadStream()
        {
            return NetworkStream;
        }

        protected virtual Stream GetWriteStream()
        {
            return NetworkStream;
        }

        public Task<MiddlemanRequest> ReadRequestAsync()
        {
            return ReadRequestAsync(CancellationToken.None);
        }

        public Task<MiddlemanRequest> ReadRequestAsync(CancellationToken ct)
        {
            var requestParser = new MiddlemanRequestParser();

            return requestParser.ParseAsync(this, GetReadStream());
        }

        public async Task WriteResponseAsync(MiddlemanResponse response)
        {
            await WriteResponseAsync(response, CancellationToken.None)
                .ConfigureAwait(false);
        }

        public Version RequestVersion;

        public async Task WriteResponseAsync(MiddlemanResponse response, CancellationToken ct)
        {
            var ms = new MemoryStream();
            var sw = new StreamWriter(ms, HeaderEncoding) { NewLine = "\r\n" };

            sw.WriteLine("HTTP/{0} {1} {2}", RequestVersion, response.StatusCode, response.StatusDescription);

            for (var i = 0; i < response.Headers.Count; i++)
            {
                var key = response.Headers.GetKey(i);
                var val = response.Headers.Get(i);

                if (!key.Equals("X-AspNet-Version", StringComparison.InvariantCultureIgnoreCase) &&
                    !key.Equals("X-SourceFiles", StringComparison.InvariantCultureIgnoreCase) &&
                    !key.Equals("X-Powered-By", StringComparison.InvariantCultureIgnoreCase))
                {
                    sw.WriteLine("{0}: {1}", key, val);
                }
            }

            sw.WriteLine();
            sw.Flush();

            var writeStream = GetWriteStream();

            ms.Position = 0;

            var responseHeaderBytes = ms.ToArray();
            var responseHeaders = HeaderEncoding.GetString(responseHeaderBytes.Where(x => x != 0).ToArray());

            await writeStream.WriteAsync(responseHeaderBytes, 0, responseHeaderBytes.Length, ct).ConfigureAwait(false);
            Log.Debug("{0}: Wrote headers ({1}b)", RemoteEndPoint, ms.Length);

            var responseBody = "";

            if (response.ResponseBody != null && response.ResponseBody.CanRead)
            {
                var buffer = new byte[8192];
                int read;
                long written = 0;

                while (
                    (read = await response.ResponseBody.ReadAsync(buffer, 0, buffer.Length, ct).ConfigureAwait(false)) >
                    0)
                {
                    var newBytes = buffer.Where(x => x != 0).ToArray();
                    responseBody += HeaderEncoding.GetString(newBytes, 0, Math.Min(newBytes.Length, read));

                    written += read;
                    Log.Debug("{0}: Read {1:N0} bytes from response body", RemoteEndPoint, read);

                    try
                    {
                        await writeStream.WriteAsync(buffer, 0, read, ct).ConfigureAwait(false);
                        Log.Debug("{0}: Wrote {1:N0} bytes to client", RemoteEndPoint, read);
                    }
                    catch (Exception ex)
                    {
                        Log.ErrorException("Error writing to response stream", ex);
                    }

                    if (written >= response.ContentLength)
                    {
                        break;
                    }
                }

                Log.Debug("{0}: Wrote response body ({1:N0} bytes) to client", RemoteEndPoint, written);
            }

            Log.Info("RESPONSE TO CLIENT: " + Environment.NewLine + (responseHeaders.Trim() + Environment.NewLine + Environment.NewLine + responseBody.Trim()).Trim() + Environment.NewLine);

            await writeStream.FlushAsync(ct).ConfigureAwait(false);
        }

        public void Close()
        {
            Connection.Close();
        }
    }
}