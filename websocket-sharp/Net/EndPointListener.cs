#region License
/*
 * EndPointListener.cs
 *
 * This code is derived from EndPointListener.cs (System.Net) of Mono
 * (http://www.mono-project.com).
 *
 * The MIT License
 *
 * Copyright (c) 2005 Novell, Inc. (http://www.novell.com)
 * Copyright (c) 2012-2016 sta.blockhead
 *
 * Permission is hereby granted, free of charge, to any person obtaining a copy
 * of this software and associated documentation files (the "Software"), to deal
 * in the Software without restriction, including without limitation the rights
 * to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
 * copies of the Software, and to permit persons to whom the Software is
 * furnished to do so, subject to the following conditions:
 *
 * The above copyright notice and this permission notice shall be included in
 * all copies or substantial portions of the Software.
 *
 * THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
 * IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
 * FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
 * AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
 * LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
 * OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
 * THE SOFTWARE.
 */
#endregion

#region Authors
/*
 * Authors:
 * - Gonzalo Paniagua Javier <gonzalo@novell.com>
 */
#endregion

#region Contributors
/*
 * Contributors:
 * - Liryna <liryna.stark@gmail.com>
 * - Nicholas Devenish
 */
#endregion

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;

namespace WebSocketSharp.Net
{
    internal sealed class EndPointListener
    {
        #region Private Fields

        private List<HttpListenerPrefix> _all; // host == '+'
        private static readonly string _defaultCertFolderPath;
        private IPEndPoint _endpoint;
        private Dictionary<HttpListenerPrefix, HttpListener> _prefixes;
        private bool _secure;
        private Socket _socket;
        private ServerSslConfiguration _sslConfig;
        private List<HttpListenerPrefix> _unhandled; // host == '*'
        private Dictionary<HttpConnection, HttpConnection> _unregistered;
        private object _unregisteredSync;

        #endregion

        #region Static Constructor

        static EndPointListener()
        {
            _defaultCertFolderPath =
              Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        }

        #endregion

        #region Internal Constructors

        internal EndPointListener(
          IPEndPoint endpoint,
          bool secure,
          string certificateFolderPath,
          ServerSslConfiguration sslConfig,
          bool reuseAddress
        )
        {
            if (secure)
            {
                var cert = GetCertificate(endpoint.Port, certificateFolderPath, sslConfig.ServerCertificate);
                _sslConfig = new ServerSslConfiguration(sslConfig);
                _sslConfig.ServerCertificate = cert ?? throw new ArgumentException("No server certificate could be found.");
                _secure = true;
            }

            _endpoint = endpoint;
            _prefixes = new Dictionary<HttpListenerPrefix, HttpListener>();
            _unregistered = new Dictionary<HttpConnection, HttpConnection>();
            _unregisteredSync = ((ICollection)_unregistered).SyncRoot;
            _socket = new Socket(endpoint.Address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);

            if (reuseAddress)
                _socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);

            _socket.Bind(endpoint);
            _socket.Listen(500);
            _socket.BeginAccept(OnAccept, this);
        }

        #endregion

        #region Public Properties

        public IPAddress Address => _endpoint.Address;

        public bool IsSecure => _secure;

        public int Port => _endpoint.Port;

        public ServerSslConfiguration SslConfiguration => _sslConfig;

        #endregion

        #region Private Methods

        private static void AddSpecial(List<HttpListenerPrefix> prefixes, HttpListenerPrefix prefix)
        {
            var path = prefix.Path;
            foreach (var pref in prefixes)
            {
                if (pref.Path == path)
                    throw new HttpListenerException(87, "The prefix is already in use.");
            }
            prefixes.Add(prefix);
        }

        private static RSACryptoServiceProvider CreateRSAFromFile(string filename)
        {
            byte[] pvk = null;
            using (var fs = File.Open(filename, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                pvk = new byte[fs.Length];
                fs.Read(pvk, 0, pvk.Length);
            }

            var rsa = new RSACryptoServiceProvider();
            rsa.ImportCspBlob(pvk);

            return rsa;
        }

        private static X509Certificate2 GetCertificate(
            int port, string folderPath, X509Certificate2 defaultCertificate)
        {
            if (folderPath == null || folderPath.Length == 0)
                folderPath = _defaultCertFolderPath;

            try
            {
                var cer = Path.Combine(folderPath, $"{port}.cer");
                var key = Path.Combine(folderPath, $"{port}.key");
                if (File.Exists(cer) && File.Exists(key))
                {
                    var cert = new X509Certificate2(cer);
                    cert.PrivateKey = CreateRSAFromFile(key);

                    return cert;
                }
            }
            catch
            {
            }
            return defaultCertificate;
        }

        private void LeaveIfNoPrefix()
        {
            if (_prefixes.Count > 0)
                return;

            var prefs = _unhandled;
            if (prefs != null && prefs.Count > 0)
                return;

            prefs = _all;
            if (prefs != null && prefs.Count > 0)
                return;

            EndPointManager.RemoveEndPoint(_endpoint);
        }

        private static void OnAccept(IAsyncResult asyncResult)
        {
            var lsnr = (EndPointListener)asyncResult.AsyncState;

            Socket sock = null;
            try
            {
                sock = lsnr._socket.EndAccept(asyncResult);
            }
            catch (SocketException)
            {
                // TODO: Should log the error code when this class has a logging.
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            try
            {
                lsnr._socket.BeginAccept(OnAccept, lsnr);
            }
            catch
            {
                if (sock != null)
                    sock.Close();

                return;
            }

            if (sock == null)
                return;

            ProcessAccepted(sock, lsnr);
        }

        private static void ProcessAccepted(Socket socket, EndPointListener listener)
        {
            HttpConnection conn = null;
            try
            {
                conn = new HttpConnection(socket, listener);
                lock (listener._unregisteredSync)
                    listener._unregistered[conn] = conn;

                conn.BeginReadRequest();
            }
            catch
            {
                if (conn != null)
                {
                    conn.Close(true);
                    return;
                }

                socket.Close();
            }
        }

        private static bool RemoveSpecial(List<HttpListenerPrefix> prefixes, HttpListenerPrefix prefix)
        {
            var path = prefix.Path;
            var cnt = prefixes.Count;
            for (var i = 0; i < cnt; i++)
            {
                if (prefixes[i].Path == path)
                {
                    prefixes.RemoveAt(i);
                    return true;
                }
            }
            return false;
        }

        private static HttpListener SearchHttpListenerFromSpecial(
            string path, List<HttpListenerPrefix> prefixes)
        {
            if (prefixes == null)
                return null;

            HttpListener bestMatch = null;

            var bestLen = -1;
            foreach (var pref in prefixes)
            {
                var prefPath = pref.Path;

                var len = prefPath.Length;
                if (len < bestLen)
                    continue;

                if (path.StartsWith(prefPath))
                {
                    bestLen = len;
                    bestMatch = pref.Listener;
                }
            }
            return bestMatch;
        }

        #endregion

        #region Internal Methods

        internal static bool CertificateExists(int port, string folderPath)
        {
            if (folderPath == null || folderPath.Length == 0)
                folderPath = _defaultCertFolderPath;

            var cer = Path.Combine(folderPath, string.Format("{0}.cer", port));
            var key = Path.Combine(folderPath, string.Format("{0}.key", port));

            return File.Exists(cer) && File.Exists(key);
        }

        internal void RemoveConnection(HttpConnection connection)
        {
            lock (_unregisteredSync)
                _unregistered.Remove(connection);
        }

        internal bool TrySearchHttpListener(Uri uri, out HttpListener listener)
        {
            listener = null;

            if (uri == null)
                return false;

            var host = uri.Host;
            var dns = Uri.CheckHostName(host) == UriHostNameType.Dns;
            var port = uri.Port.ToString();
            var path = HttpUtility.UrlDecode(uri.AbsolutePath, Encoding.UTF8);
            var pathSlash = path[path.Length - 1] != '/' ? path + "/" : path;

            if (host != null && host.Length > 0)
            {
                var bestLen = -1;
                foreach (var pref in _prefixes.Keys)
                {
                    if (dns)
                    {
                        var prefHost = pref.Host;
                        if (Uri.CheckHostName(prefHost) == UriHostNameType.Dns && prefHost != host)
                            continue;
                    }

                    if (pref.Port != port)
                        continue;

                    var prefPath = pref.Path;

                    var len = prefPath.Length;
                    if (len < bestLen)
                        continue;

                    if (path.StartsWith(prefPath) || pathSlash.StartsWith(prefPath))
                    {
                        bestLen = len;
                        listener = _prefixes[pref];
                    }
                }

                if (bestLen != -1)
                    return true;
            }

            var prefs = _unhandled;
            listener = SearchHttpListenerFromSpecial(path, prefs);
            if (listener == null && pathSlash != path)
                listener = SearchHttpListenerFromSpecial(pathSlash, prefs);

            if (listener != null)
                return true;

            prefs = _all;
            listener = SearchHttpListenerFromSpecial(path, prefs);
            if (listener == null && pathSlash != path)
                listener = SearchHttpListenerFromSpecial(pathSlash, prefs);

            return listener != null;
        }

        #endregion

        #region Public Methods

        public void AddPrefix(HttpListenerPrefix prefix, HttpListener listener)
        {
            List<HttpListenerPrefix> current, future;
            if (prefix.Host == "*")
            {
                do
                {
                    current = _unhandled;
                    future = current != null
                             ? new List<HttpListenerPrefix>(current)
                             : new List<HttpListenerPrefix>();

                    prefix.Listener = listener;
                    AddSpecial(future, prefix);
                }
                while (Interlocked.CompareExchange(ref _unhandled, future, current) != current);

                return;
            }

            if (prefix.Host == "+")
            {
                do
                {
                    current = _all;
                    future = current != null
                             ? new List<HttpListenerPrefix>(current)
                             : new List<HttpListenerPrefix>();

                    prefix.Listener = listener;
                    AddSpecial(future, prefix);
                }
                while (Interlocked.CompareExchange(ref _all, future, current) != current);

                return;
            }

            Dictionary<HttpListenerPrefix, HttpListener> prefs, prefs2;
            do
            {
                prefs = _prefixes;
                if (prefs.ContainsKey(prefix))
                {
                    if (prefs[prefix] != listener)
                    {
                        throw new HttpListenerException(
                          87, string.Format("There's another listener for {0}.", prefix)
                        );
                    }

                    return;
                }

                prefs2 = new Dictionary<HttpListenerPrefix, HttpListener>(prefs);
                prefs2[prefix] = listener;
            }
            while (Interlocked.CompareExchange(ref _prefixes, prefs2, prefs) != prefs);
        }

        public void Close()
        {
            _socket.Close();

            HttpConnection[] conns = null;
            lock (_unregisteredSync)
            {
                if (_unregistered.Count == 0)
                    return;

                var keys = _unregistered.Keys;
                conns = new HttpConnection[keys.Count];
                keys.CopyTo(conns, 0);
                _unregistered.Clear();
            }

            for (var i = conns.Length - 1; i >= 0; i--)
                conns[i].Close(true);
        }

        public void RemovePrefix(HttpListenerPrefix prefix)
        {
            List<HttpListenerPrefix> current, future;
            if (prefix.Host == "*")
            {
                do
                {
                    current = _unhandled;
                    if (current == null)
                        break;

                    future = new List<HttpListenerPrefix>(current);
                    if (!RemoveSpecial(future, prefix))
                        break; // The prefix wasn't found.
                }
                while (Interlocked.CompareExchange(ref _unhandled, future, current) != current);

                LeaveIfNoPrefix();
                return;
            }

            if (prefix.Host == "+")
            {
                do
                {
                    current = _all;
                    if (current == null)
                        break;

                    future = new List<HttpListenerPrefix>(current);
                    if (!RemoveSpecial(future, prefix))
                        break; // The prefix wasn't found.
                }
                while (Interlocked.CompareExchange(ref _all, future, current) != current);

                LeaveIfNoPrefix();
                return;
            }

            Dictionary<HttpListenerPrefix, HttpListener> prefs, prefs2;
            do
            {
                prefs = _prefixes;
                if (!prefs.ContainsKey(prefix))
                    break;

                prefs2 = new Dictionary<HttpListenerPrefix, HttpListener>(prefs);
                prefs2.Remove(prefix);
            }
            while (Interlocked.CompareExchange(ref _prefixes, prefs2, prefs) != prefs);

            LeaveIfNoPrefix();
        }

        #endregion
    }
}