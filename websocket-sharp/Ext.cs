#region License
/*
 * Ext.cs
 *
 * Some parts of this code are derived from Mono (http://www.mono-project.com):
 * - GetStatusDescription is derived from HttpListenerResponse.cs (System.Net)
 * - IsPredefinedScheme is derived from Uri.cs (System)
 * - MaybeUri is derived from Uri.cs (System)
 *
 * The MIT License
 *
 * Copyright (c) 2001 Garrett Rooney
 * Copyright (c) 2003 Ian MacLean
 * Copyright (c) 2003 Ben Maurer
 * Copyright (c) 2003, 2005, 2009 Novell, Inc. (http://www.novell.com)
 * Copyright (c) 2009 Stephane Delcroix
 * Copyright (c) 2010-2016 sta.blockhead
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

#region Contributors
/*
 * Contributors:
 * - Liryna <liryna.stark@gmail.com>
 * - Nikola Kovacevic <nikolak@outlook.com>
 * - Chris Swiedler
 */
#endregion

using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.IO.Compression;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using WebSocketSharp.Memory;
using WebSocketSharp.Net;

namespace WebSocketSharp
{
    /// <summary>
    /// Provides a set of static methods for websocket-sharp.
    /// </summary>
    public static class Ext
    {
        public static Encoding PlainUTF8 { get; } =
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

        #region Private Fields

        private static CompressionMethod[] _compressionMethods =
            (CompressionMethod[])Enum.GetValues(typeof(CompressionMethod));

        private static readonly byte[] _last = new byte[] { 0x00 };
        private const string _tspecials = "()<>@,;:\\\"/[]?={} \t";
        public static readonly char[] QueryFragmentComponents = new[] { '?', '#' };

        #endregion

        #region Private Methods

        private static byte[] Compress(this byte[] data)
        {
            if (data.Length == 0)
                //return new byte[] { 0x00, 0x00, 0x00, 0xff, 0xff };
                return data;

            using (var input = RecyclableMemoryManager.Shared.GetStream(data))
                return input.CompressToArray();
        }

        private static RecyclableMemoryStream Compress(this Stream stream)
        {
            var output = RecyclableMemoryManager.Shared.GetStream();
            try
            {
                if (stream.Length == 0)
                    return output;

                stream.Position = 0;
                using (var ds = new DeflateStream(output, CompressionLevel.Fastest, true))
                    stream.CopyBytesTo(ds);

                // BFINAL set to 1.
                output.Write(_last, 0, 1);

                output.Position = 0;
                return output;
            }
            catch
            {
                output.Dispose();
                throw;
            }
        }

        private static byte[] CompressToArray(this Stream stream)
        {
            using (var output = stream.Compress())
            {
                output.Close();
                return output.ToArray();
            }
        }

        private static RecyclableMemoryStream Decompress(this Stream stream)
        {
            var output = RecyclableMemoryManager.Shared.GetStream();
            try
            {
                if (stream.Length == 0)
                    return output;

                stream.Position = 0;
                using (var ds = new DeflateStream(stream, CompressionMode.Decompress, true))
                {
                    ds.CopyBytesTo(output);
                    output.Position = 0;

                    return output;
                }
            }
            catch
            {
                output.Dispose();
                throw;
            }
        }

        private static bool IsHttpMethod(this string value)
        {
            return value == "GET"
                || value == "HEAD"
                || value == "POST"
                || value == "PUT"
                || value == "DELETE"
                || value == "CONNECT"
                || value == "OPTIONS"
                || value == "TRACE";
        }

        private static bool IsHttpMethod10(this string value)
        {
            return value == "GET"
                || value == "HEAD"
                || value == "POST";
        }

        #endregion

        #region Internal Methods

        internal static byte[] Append(this ushort code, string reason)
        {
            Span<byte> resultSpan = stackalloc byte[2];
            resultSpan.Write(code, ByteOrder.Big);

            if (reason != null && reason.Length > 0)
            {
                int strByteCount = Encoding.UTF8.GetByteCount(reason);
                byte[] byteBuffer = new byte[2 + strByteCount];
                resultSpan.CopyTo(byteBuffer);

                Encoding.UTF8.GetBytes(reason, 0, reason.Length, byteBuffer, byteIndex: 2);
                return byteBuffer;
            }
            return resultSpan.ToArray();
        }

        internal static void Close(this HttpListenerResponse response, HttpStatusCode code)
        {
            response.StatusCode = (int)code;
            response.OutputStream.Close();
        }

        internal static void CloseWithAuthChallenge(
            this HttpListenerResponse response, string challenge)
        {
            response.Headers.InternalSet("WWW-Authenticate", challenge, true);
            response.Close(HttpStatusCode.Unauthorized);
        }

        internal static byte[] Compress(this byte[] data, CompressionMethod method)
        {
            return method == CompressionMethod.Deflate
                ? data.Compress()
                : data;
        }

        internal static Stream Compress(this Stream stream, CompressionMethod method)
        {
            return method == CompressionMethod.Deflate
                ? stream.Compress()
                : stream;
        }

        /// <summary>
        /// Determines whether the specified string contains any of characters in
        /// the specified array of <see cref="char"/>.
        /// </summary>
        /// <returns>
        /// <c>true</c> if <paramref name="value"/> contains any of characters in
        /// <paramref name="anyOf"/>; otherwise, <c>false</c>.
        /// </returns>
        /// <param name="value">
        /// A <see cref="string"/> to test.
        /// </param>
        /// <param name="anyOf">
        /// An array of <see cref="char"/> that contains one or more characters to
        /// seek.
        /// </param>
        internal static bool Contains(this string value, params char[] anyOf)
        {
            return anyOf != null && anyOf.Length > 0
                ? value.IndexOfAny(anyOf) > -1
                : false;
        }

        internal static bool Contains(
            this NameValueCollection collection, string name)
        {
            return collection[name] != null;
        }

        internal static bool Contains(
            this NameValueCollection collection,
            string name,
            string value,
            StringComparison comparisonTypeForValue)
        {
            string val = collection[name];
            if (val == null)
                return false;

            foreach (string elm in val.Split(','))
            {
                if (elm.AsSpan().Trim().Equals(value.AsSpan(), comparisonTypeForValue))
                    return true;
            }

            return false;
        }

        internal static bool Contains<T>(this IEnumerable<T> source, Func<T, bool> condition)
        {
            if (source is IReadOnlyList<T> list)
            {
                for (int i = 0; i < list.Count; i++)
                    if (condition(list[i]))
                        return true;
            }
            else
            {
                foreach (T elm in source)
                {
                    if (condition(elm))
                        return true;
                }
            }
            return false;
        }

        internal static bool ContainsTwice(this string[] values)
        {
            var len = values.Length;
            var end = len - 1;
            bool seek(int idx)
            {
                if (idx == end)
                    return false;

                string val = values[idx];
                for (var i = idx + 1; i < len; i++)
                {
                    if (values[i] == val)
                        return true;
                }

                return seek(++idx);
            }

            return seek(0);
        }

        internal static T[] Copy<T>(this T[] source, int length)
        {
            var dest = new T[length];
            Array.Copy(source, 0, dest, 0, length);
            return dest;
        }

        internal static T[] Copy<T>(this T[] source, long count)
        {
            var dest = new T[count];
            Array.Copy(source, 0, dest, 0, count);
            return dest;
        }

        internal static Stream Decompress(this Stream stream, CompressionMethod method)
        {
            return method == CompressionMethod.Deflate
                ? stream.Decompress()
                : stream;
        }

        internal static RecyclableMemoryStream Decompress(
            this ReadOnlySpan<byte> source, CompressionMethod method)
        {
            var result = RecyclableMemoryManager.Shared.GetStream();
            try
            {
                result.Write(source);
                result.Position = 0;

                if (method == CompressionMethod.Deflate)
                {
                    var decompressed = result.Decompress();
                    result.Dispose();
                    return decompressed;
                }
                return result;
            }
            catch
            {
                result.Dispose();
                throw;
            }
        }

        internal static Stream Decompress(this ReadOnlyMemory<byte> source, CompressionMethod method)
        {
            return Decompress(source.Span, method);
        }

        /// <summary>
        /// Determines whether the specified <see cref="int"/> equals the specified <see cref="char"/>,
        /// and invokes the specified <c>Action&lt;int&gt;</c> delegate at the same time.
        /// </summary>
        /// <returns>
        /// <c>true</c> if <paramref name="value"/> equals <paramref name="c"/>;
        /// otherwise, <c>false</c>.
        /// </returns>
        /// <param name="value">
        /// An <see cref="int"/> to compare.
        /// </param>
        /// <param name="c">
        /// A <see cref="char"/> to compare.
        /// </param>
        /// <param name="action">
        /// An <c>Action&lt;int&gt;</c> delegate that references the method(s) called
        /// at the same time as comparing. An <see cref="int"/> parameter to pass to
        /// the method(s) is <paramref name="value"/>.
        /// </param>
        internal static bool EqualsWith(this int value, char c, Action<int> action)
        {
            action(value);
            return value == c - 0;
        }

        /// <summary>
        /// Gets the absolute path from the specified <see cref="Uri"/>.
        /// </summary>
        /// <returns>
        /// A <see cref="string"/> that represents the absolute path if it's successfully found;
        /// otherwise, <see langword="null"/>.
        /// </returns>
        /// <param name="uri">
        /// A <see cref="Uri"/> that represents the URI to get the absolute path from.
        /// </param>
        internal static string GetAbsolutePath(this Uri uri)
        {
            if (uri.IsAbsoluteUri)
                return uri.AbsolutePath;

            var original = uri.OriginalString;
            if (original[0] != '/')
                return null;

            int idx = original.IndexOfAny(QueryFragmentComponents);
            return idx > 0 ? original.Substring(0, idx) : original;
        }

        internal static CookieCollection GetCookies(
            this NameValueCollection headers, bool response)
        {
            var val = headers[response ? "Set-Cookie" : "Cookie"];
            return val != null
                ? CookieCollection.Parse(val, response)
                : new CookieCollection();
        }

        internal static string GetDnsSafeHost(this Uri uri, bool bracketIPv6)
        {
            return bracketIPv6 && uri.HostNameType == UriHostNameType.IPv6
                ? uri.Host
                : uri.DnsSafeHost;
        }

        internal static string GetMessage(this CloseStatusCode code)
        {
            return code == CloseStatusCode.ProtocolError
                   ? "A WebSocket protocol error has occurred."
                   : code == CloseStatusCode.UnsupportedData
                     ? "Unsupported data has been received."
                     : code == CloseStatusCode.Abnormal
                       ? "An exception has occurred."
                       : code == CloseStatusCode.InvalidData
                         ? "Invalid data has been received."
                         : code == CloseStatusCode.PolicyViolation
                           ? "A policy violation has occurred."
                           : code == CloseStatusCode.TooBig
                             ? "A too big message has been received."
                             : code == CloseStatusCode.MandatoryExtension
                               ? "WebSocket client didn't receive expected extension(s)."
                               : code == CloseStatusCode.ServerError
                                 ? "WebSocket server got an internal error."
                                 : code == CloseStatusCode.TlsHandshakeFailure
                                   ? "An error has occurred during a TLS handshake."
                                   : string.Empty;
        }

        /// <summary>
        /// Gets the value from the specified string that contains a pair of
        /// name and value separated by a character.
        /// </summary>
        /// <returns>
        ///   <para>
        ///   A <see cref="string"/> that represents the value.
        ///   </para>
        ///   <para>
        ///   <see langword="null"/> if the value is not present.
        ///   </para>
        /// </returns>
        /// <param name="nameAndValue">
        /// A <see cref="string"/> that contains a pair of name and value.
        /// </param>
        /// <param name="separator">
        /// A <see cref="char"/> used to separate name and value.
        /// </param>
        /// <param name="unquote">
        /// A <see cref="bool"/>: <c>true</c> if unquotes the value; otherwise,
        /// <c>false</c>.
        /// </param>
        internal static ReadOnlySpan<char> GetValue(
            this ReadOnlySpan<char> nameAndValue, char separator, bool unquote)
        {
            int idx = nameAndValue.IndexOf(separator);
            if (idx < 0 || idx == nameAndValue.Length - 1)
                return null;

            var val = nameAndValue.Slice(idx + 1).Trim();
            return unquote ? val.Unquote() : val;
        }

        internal static bool IsCompressionExtension(
            this string value, CompressionMethod method)
        {
            return value.StartsWith(method.ToExtensionString());
        }

        internal static bool IsControl(this byte opcode)
        {
            return opcode > 0x7 && opcode < 0x10;
        }

        internal static bool IsControl(this OpCode opcode)
        {
            return opcode >= OpCode.Close;
        }

        internal static bool IsData(this byte opcode)
        {
            return opcode == 0x1 || opcode == 0x2;
        }

        internal static bool IsData(this OpCode opcode)
        {
            return opcode == OpCode.Text || opcode == OpCode.Binary;
        }

        internal static bool IsHttpMethod(this string value, Version version)
        {
            return version == HttpVersion.Version10
                   ? value.IsHttpMethod10()
                   : value.IsHttpMethod();
        }

        internal static bool IsPortNumber(this int value)
        {
            return value > 0 && value < 65536;
        }

        internal static bool IsReserved(this ushort code)
        {
            return code == 1004
                || code == 1005
                || code == 1006
                || code == 1015;
        }

        internal static bool IsReserved(this CloseStatusCode code)
        {
            return code == CloseStatusCode.Undefined
                || code == CloseStatusCode.NoStatus
                || code == CloseStatusCode.Abnormal
                || code == CloseStatusCode.TlsHandshakeFailure;
        }

        internal static bool IsSupported(this byte opcode)
        {
            return Enum.IsDefined(typeof(OpCode), opcode);
        }

        internal static bool IsText(this ReadOnlySpan<char> value)
        {
            for (var i = 0; i < value.Length; i++)
            {
                var c = value[i];
                if (c < 0x20)
                {
                    if ("\r\n\t".IndexOf(c) == -1)
                        return false;

                    if (c == '\n')
                    {
                        i++;
                        if (i == value.Length)
                            break;

                        c = value[i];
                        if (" \t".IndexOf(c) == -1)
                            return false;
                    }
                    continue;
                }

                if (c == 0x7f)
                    return false;
            }

            return true;
        }

        internal static bool IsToken(this ReadOnlySpan<char> value)
        {
            foreach (char c in value)
            {
                if (c < 0x20)
                    return false;

                if (c > 0x7e)
                    return false;

                if (_tspecials.IndexOf(c) > -1)
                    return false;
            }
            return true;
        }

        internal static bool KeepsAlive(
            this NameValueCollection headers, Version version)
        {
            var comparison = StringComparison.OrdinalIgnoreCase;
            return version < HttpVersion.Version11
                ? headers.Contains("Connection", "keep-alive", comparison)
                : !headers.Contains("Connection", "close", comparison);
        }

        internal static string Quote(this string value)
        {
            return string.Format("\"{0}\"", value.Replace("\"", "\\\""));
        }

        internal static bool TryReadByte(this Stream stream, out byte value)
        {
            int b = stream.ReadByte();
            value = (byte)b;
            return b != -1;
        }

        internal static RecyclableMemoryStream ReadBytes(this Stream stream, long length)
        {
            byte[] buffer = RecyclableMemoryManager.Shared.GetBlock();
            var result = RecyclableMemoryManager.Shared.GetStream();
            try
            {
                while (result.Length < length)
                {
                    int toRead = (int)Math.Min(buffer.Length, length - result.Length);
                    int read = stream.Read(buffer, 0, toRead);
                    if (read == 0)
                        break;
                    result.Write(buffer, 0, read);
                }

                result.Position = 0;
                return result;
            }
            catch
            {
                result?.Dispose();
                throw;
            }
            finally
            {
                RecyclableMemoryManager.Shared.ReturnBlock(buffer);
            }
        }

        internal static bool ReadBytes(
            this Stream stream, byte[] output, int offset, int count)
        {
            if (count > output.Length)
                throw new ArgumentOutOfRangeException(nameof(count));

            if(offset + count > output.Length)
                throw new ArgumentOutOfRangeException(nameof(offset));

            while (count > 0)
            {
                int read = stream.Read(output, offset, count);
                if (read == 0)
                    return false;

                count -= read;
                offset += read;
            }
            return true;
        }

        internal static T[] Reverse<T>(this T[] array)
        {
            var len = array.Length;
            var ret = new T[len];

            var end = len - 1;
            for (var i = 0; i <= end; i++)
                ret[i] = array[end - i];

            return ret;
        }

        // TODO: reduce allocs
        internal static IEnumerable<string> SplitHeaderValue(
            this string value, params char[] separators)
        {
            int len = value.Length;
            var buff = new StringBuilder(32);
            var end = len - 1;
            var escaped = false;
            var quoted = false;

            for (var i = 0; i <= end; i++)
            {
                var c = value[i];
                buff.Append(c);

                if (c == '"')
                {
                    if (escaped)
                    {
                        escaped = false;
                        continue;
                    }

                    quoted = !quoted;
                    continue;
                }

                if (c == '\\')
                {
                    if (i == end)
                        break;

                    if (value[i + 1] == '"')
                        escaped = true;

                    continue;
                }

                if (Array.IndexOf(separators, c) > -1)
                {
                    if (quoted)
                        continue;

                    buff.Length -= 1;
                    yield return buff.ToString();

                    buff.Length = 0;
                    continue;
                }
            }

            yield return buff.ToString();
        }

        internal static RecyclableMemoryStream CopyToMemory(this Stream stream)
        {
            var output = RecyclableMemoryManager.Shared.GetStream();
            try
            {
                stream.Position = 0;
                stream.CopyBytesTo(output);
                return output;
            }
            catch
            {
                output.Dispose();
                throw;
            }
        }

        internal static RecyclableMemoryStream ToMemory(this StringBuilder builder, Encoding encoding)
        {
            char[] charBuffer = RecyclableMemoryManager.Shared.GetCharBlock();
            byte[] byteBuffer = RecyclableMemoryManager.Shared.GetBlock();
            var stream = RecyclableMemoryManager.Shared.GetStream();
            try
            {
                int left = builder.Length;
                int offset = 0;
                while (left > 0)
                {
                    int toCopy = Math.Min(charBuffer.Length, left);
                    builder.CopyTo(offset, charBuffer, destinationIndex: 0, toCopy);

                    int bytesWritten = encoding.GetBytes(charBuffer, 0, toCopy, byteBuffer, byteIndex: 0);
                    stream.Write(byteBuffer, 0, bytesWritten);

                    int charsWritten = encoding.GetCharCount(byteBuffer, 0, bytesWritten);
                    left -= charsWritten;
                    offset += charsWritten;
                }
                return stream;
            }
            catch
            {
                stream?.Dispose();
                throw;
            }
            finally
            {
                RecyclableMemoryManager.Shared.ReturnBlock(byteBuffer);
                RecyclableMemoryManager.Shared.ReturnBlock(charBuffer);
            }
        }

        internal static byte[] CopyToArray(this Stream stream)
        {
            using (var output = CopyToMemory(stream))
                return output.ToArray();
        }

        internal static CompressionMethod ToCompressionMethod(this string value)
        {
            foreach (CompressionMethod method in _compressionMethods)
            {
                if (method.ToExtensionString() == value)
                    return method;
            }
            return CompressionMethod.None;
        }

        internal static string ToExtensionString(
            this CompressionMethod method, params string[] parameters)
        {
            if (method == CompressionMethod.None)
                return string.Empty;

            string name = $"permessage-{method.ToString().ToLower()}";

            return parameters != null && parameters.Length > 0
                   ? $"{name}; {parameters.ToString("; ")}"
                   : name;
        }

        internal static System.Net.IPAddress ToIPAddress(this string value)
        {
            if (value == null || value.Length == 0)
                return null;

            if (System.Net.IPAddress.TryParse(value, out System.Net.IPAddress addr))
                return addr;

            try
            {
                var addrs = System.Net.Dns.GetHostAddresses(value);
                return addrs[0];
            }
            catch
            {
                return null;
            }
        }

        internal static List<TSource> ToList<TSource>(this IEnumerable<TSource> source)
        {
            return new List<TSource>(source);
        }

        internal static string ToString(
            this System.Net.IPAddress address, bool bracketIPv6)
        {
            return bracketIPv6 && address.AddressFamily == AddressFamily.InterNetworkV6
                ? string.Format("[{0}]", address.ToString())
                : address.ToString();
        }
        
        internal static ushort ToUInt16(this byte[] source, ByteOrder sourceOrder)
        {
            return BitConverter.ToUInt16(source.ToHostOrder(sourceOrder), 0);
        }

        internal static ulong ToUInt64(this byte[] source, ByteOrder sourceOrder)
        {
            return BitConverter.ToUInt64(source.ToHostOrder(sourceOrder), 0);
        }

        internal static IEnumerable<string> TrimAll(this IEnumerable<string> source)
        {
            if (source is IReadOnlyList<string> list)
            {
                for (int i = 0; i < list.Count; i++)
                    yield return list[i].Trim();
            }
            else
            {
                foreach (var elm in source)
                    yield return elm.Trim();
            }
        }

        internal static string TrimSlashFromEnd(this string value)
        {
            var ret = value.TrimEnd('/');
            return ret.Length > 0 ? ret : "/";
        }

        private static readonly char[] _slashedEndChars = new [] { '/', '\\' };

        internal static ReadOnlySpan<char> TrimSlashOrBackslashFromEnd(this ReadOnlySpan<char> value)
        {
            var ret = value.TrimEnd(_slashedEndChars);
            return ret.Length > 0 ? ret : value.Slice(0, 1);
        }

        internal static bool TryCreateVersion(
            this string versionString, out Version result)
        {
            try
            {
                result = new Version(versionString);
                return true;
            }
            catch
            {
                result = null;
                return false;
            }
        }

        /// <summary>
        /// Tries to create a new <see cref="Uri"/> for WebSocket with
        /// the specified <paramref name="uriString"/>.
        /// </summary>
        /// <returns>
        /// <c>true</c> if the <see cref="Uri"/> was successfully created;
        /// otherwise, <c>false</c>.
        /// </returns>
        /// <param name="uriString">
        /// A <see cref="string"/> that represents a WebSocket URL to try.
        /// </param>
        /// <param name="result">
        /// When this method returns, a <see cref="Uri"/> that
        /// represents the WebSocket URL or <see langword="null"/>
        /// if <paramref name="uriString"/> is invalid.
        /// </param>
        /// <param name="message">
        /// When this method returns, a <see cref="string"/> that
        /// represents an error message or <see langword="null"/>
        /// if <paramref name="uriString"/> is valid.
        /// </param>
        internal static bool TryCreateWebSocketUri(
            this string uriString, out Uri result, out string message)
        {
            result = null;

            var uri = uriString.ToUri();
            if (uri == null)
            {
                message = "An invalid URI string.";
                return false;
            }

            if (!uri.IsAbsoluteUri)
            {
                message = "A relative URI.";
                return false;
            }

            var schm = uri.Scheme;
            if (!(schm == "ws" || schm == "wss"))
            {
                message = "The scheme part is not 'ws' or 'wss'.";
                return false;
            }

            var port = uri.Port;
            if (port == 0)
            {
                message = "The port part is zero.";
                return false;
            }

            if (uri.Fragment.Length > 0)
            {
                message = "It includes the fragment component.";
                return false;
            }

            message = null;
            result = port != -1
                     ? uri
                     : new Uri($"{schm}://{uri.Host}:{(schm == "ws" ? 80 : 443).ToString()}{uri.PathAndQuery}");

            return true;
        }

        internal static bool TryGetUTF8DecodedString(this byte[] bytes, out string s)
        {
            try
            {
                s = Encoding.UTF8.GetString(bytes);
                return true;
            }
            catch
            {
                s = null;
                return false;
            }
        }

        internal static bool TryGetUTF8EncodedBytes(this string s, out byte[] bytes)
        {
            try
            {
                bytes = Encoding.UTF8.GetBytes(s);
                return true;
            }
            catch
            {
                bytes = null;
                return false;
            }
        }

        internal static bool TryOpenRead(
            this FileInfo fileInfo, out FileStream fileStream)
        {
            try
            {
                fileStream = fileInfo.OpenRead();
                return true;
            }
            catch
            {
                fileStream = null;
                return false;
            }
        }

        internal static StringBuilder Append(this StringBuilder builder, ReadOnlySpan<char> span)
        {
            char[] buffer = RecyclableMemoryManager.Shared.GetCharBlock();
            try
            {
                int offset = 0;
                int left = span.Length;
                while (left > 0)
                {
                    int toAppend = Math.Min(buffer.Length, left);
                    span.Slice(offset, toAppend).CopyTo(buffer);

                    builder.Append(buffer, 0, toAppend);
                    offset += toAppend;
                    left -= toAppend;
                }
                return builder;
            }
            finally
            {
                RecyclableMemoryManager.Shared.ReturnBlock(buffer);
            }
        }

        internal static ReadOnlySpan<char> Unquote(this ReadOnlySpan<char> value)
        {
            int start = value.IndexOf('"');
            if (start == -1)
                return value;

            int end = value.LastIndexOf('"');
            if (end == start)
                return value;

            int len = end - start - 1;

            ReadOnlySpan<char> Sliced(ReadOnlySpan<char> v)
            {
                var sliced = v.Slice(start + 1, len);
                var builder = new StringBuilder(sliced.Length);
                builder.Append(sliced);
                builder.Replace("\\\"", "\"");
                return builder.ToString().AsSpan();
            }

            return len > 0
                   ? Sliced(value)
                   : string.Empty.AsSpan();
        }

        internal static bool Upgrades(
            this NameValueCollection headers, string protocol)
        {
            var comparison = StringComparison.OrdinalIgnoreCase;
            return headers.Contains("Upgrade", protocol, comparison)
                && headers.Contains("Connection", "Upgrade", comparison);
        }

        internal static string UrlDecode(this string value, Encoding encoding)
        {
            return HttpUtility.UrlDecode(value, encoding);
        }

        internal static string UrlEncode(this string value, Encoding encoding)
        {
            return HttpUtility.UrlEncode(value, encoding);
        }

        internal static string UTF8Decode(this byte[] bytes)
        {
            try
            {
                return Encoding.UTF8.GetString(bytes);
            }
            catch
            {
                return null;
            }
        }

        internal static string UTF8Decode(this byte[] bytes, int index, int count)
        {
            try
            {
                return Encoding.UTF8.GetString(bytes, index, count);
            }
            catch
            {
                return null;
            }
        }

        internal static byte[] UTF8Encode(this string s)
        {
            return Encoding.UTF8.GetBytes(s);
        }

        public static void CopyBytesTo(this Stream source, Stream destination)
        {
            byte[] buffer = RecyclableMemoryManager.Shared.GetBlock();
            try
            {
                int read;
                while ((read = source.Read(buffer, 0, buffer.Length)) != 0)
                    destination.Write(buffer, 0, read);
            }
            finally
            {
                RecyclableMemoryManager.Shared.ReturnBlock(buffer);
            }
        }

        public static void Write(this Stream destination, Stream source, long count)
        {
            byte[] buffer = RecyclableMemoryManager.Shared.GetBlock();
            try
            {
                while (count > 0)
                {
                    int toRead = (int)Math.Min(buffer.Length, count);
                    int read = source.Read(buffer, 0, toRead);
                    if (read == 0)
                        throw new EndOfStreamException();

                    destination.Write(buffer, 0, read);
                    count -= read;
                }
            }
            finally
            {
                RecyclableMemoryManager.Shared.ReturnBlock(buffer);
            }
        }

        internal static void Write(this Stream stream, ReadOnlySpan<byte> bytes)
        {
            if (bytes.IsEmpty)
                return;

            if(bytes.Length == 1)
            {
                stream.WriteByte(bytes[0]);
                return;
            }

            byte[] buffer = RecyclableMemoryManager.Shared.GetBlock();
            try
            {
                int written = 0;
                while (written < bytes.Length)
                {
                    int toWrite = Math.Min(buffer.Length, bytes.Length - written);
                    bytes.Slice(written, toWrite).CopyTo(buffer);
                    stream.Write(buffer, 0, toWrite);
                    written += toWrite;
                }
            }
            finally
            {
                RecyclableMemoryManager.Shared.ReturnBlock(buffer);
            }
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// Emits the specified <see cref="EventHandler"/> delegate if it isn't <see langword="null"/>.
        /// </summary>
        /// <param name="eventHandler">
        /// A <see cref="EventHandler"/> to emit.
        /// </param>
        /// <param name="sender">
        /// An <see cref="object"/> from which emits this <paramref name="eventHandler"/>.
        /// </param>
        /// <param name="e">
        /// A <see cref="EventArgs"/> that contains no event data.
        /// </param>
        public static void Emit(this EventHandler eventHandler, object sender, EventArgs e)
        {
            eventHandler?.Invoke(sender, e);
        }

        /// <summary>
        /// Emits the specified <c>EventHandler&lt;TEventArgs&gt;</c> delegate if it isn't
        /// <see langword="null"/>.
        /// </summary>
        /// <param name="eventHandler">
        /// An <c>EventHandler&lt;TEventArgs&gt;</c> to emit.
        /// </param>
        /// <param name="sender">
        /// An <see cref="object"/> from which emits this <paramref name="eventHandler"/>.
        /// </param>
        /// <param name="e">
        /// A <c>TEventArgs</c> that represents the event data.
        /// </param>
        /// <typeparam name="T">
        /// The type of the event data generated by the event.
        /// </typeparam>
        public static void Emit<T>(
            this EventHandler<T> eventHandler, object sender, T e)
        {
            eventHandler?.Invoke(sender, e);
        }

        /// <summary>
        /// Gets the description of the specified HTTP status <paramref name="code"/>.
        /// </summary>
        /// <returns>
        /// A <see cref="string"/> that represents the description of the HTTP status code.
        /// </returns>
        /// <param name="code">
        /// One of <see cref="HttpStatusCode"/> enum values, indicates the HTTP status code.
        /// </param>
        public static string GetDescription(this HttpStatusCode code)
        {
            return ((int)code).GetStatusDescription();
        }

        /// <summary>
        /// Gets the description of the specified HTTP status <paramref name="code"/>.
        /// </summary>
        /// <returns>
        /// A <see cref="string"/> that represents the description of the HTTP status code.
        /// </returns>
        /// <param name="code">
        /// An <see cref="int"/> that represents the HTTP status code.
        /// </param>
        public static string GetStatusDescription(this int code)
        {
            switch (code)
            {
                case 100: return "Continue";
                case 101: return "Switching Protocols";
                case 102: return "Processing";
                case 200: return "OK";
                case 201: return "Created";
                case 202: return "Accepted";
                case 203: return "Non-Authoritative Information";
                case 204: return "No Content";
                case 205: return "Reset Content";
                case 206: return "Partial Content";
                case 207: return "Multi-Status";
                case 300: return "Multiple Choices";
                case 301: return "Moved Permanently";
                case 302: return "Found";
                case 303: return "See Other";
                case 304: return "Not Modified";
                case 305: return "Use Proxy";
                case 307: return "Temporary Redirect";
                case 400: return "Bad Request";
                case 401: return "Unauthorized";
                case 402: return "Payment Required";
                case 403: return "Forbidden";
                case 404: return "Not Found";
                case 405: return "Method Not Allowed";
                case 406: return "Not Acceptable";
                case 407: return "Proxy Authentication Required";
                case 408: return "Request Timeout";
                case 409: return "Conflict";
                case 410: return "Gone";
                case 411: return "Length Required";
                case 412: return "Precondition Failed";
                case 413: return "Request Entity Too Large";
                case 414: return "Request-Uri Too Long";
                case 415: return "Unsupported Media Type";
                case 416: return "Requested Range Not Satisfiable";
                case 417: return "Expectation Failed";
                case 422: return "Unprocessable Entity";
                case 423: return "Locked";
                case 424: return "Failed Dependency";
                case 500: return "Internal Server Error";
                case 501: return "Not Implemented";
                case 502: return "Bad Gateway";
                case 503: return "Service Unavailable";
                case 504: return "Gateway Timeout";
                case 505: return "Http Version Not Supported";
                case 507: return "Insufficient Storage";
            }

            return string.Empty;
        }

        /// <summary>
        /// Determines whether the specified ushort is in the range of
        /// the status code for the WebSocket connection close.
        /// </summary>
        /// <remarks>
        ///   <para>
        ///   The ranges are the following:
        ///   </para>
        ///   <list type="bullet">
        ///     <item>
        ///       <term>
        ///       1000-2999: These numbers are reserved for definition by
        ///       the WebSocket protocol.
        ///       </term>
        ///     </item>
        ///     <item>
        ///       <term>
        ///       3000-3999: These numbers are reserved for use by libraries,
        ///       frameworks, and applications.
        ///       </term>
        ///     </item>
        ///     <item>
        ///       <term>
        ///       4000-4999: These numbers are reserved for private use.
        ///       </term>
        ///     </item>
        ///   </list>
        /// </remarks>
        /// <returns>
        /// <c>true</c> if <paramref name="value"/> is in the range of
        /// the status code for the close; otherwise, <c>false</c>.
        /// </returns>
        /// <param name="value">
        /// A <see cref="ushort"/> to test.
        /// </param>
        public static bool IsCloseStatusCode(this ushort value)
        {
            return value > 999 && value < 5000;
        }

        /// <summary>
        /// Determines whether the specified string is enclosed in
        /// the specified character.
        /// </summary>
        /// <returns>
        /// <c>true</c> if <paramref name="value"/> is enclosed in
        /// <paramref name="c"/>; otherwise, <c>false</c>.
        /// </returns>
        /// <param name="value">
        /// A <see cref="string"/> to test.
        /// </param>
        /// <param name="c">
        /// A <see cref="char"/> to find.
        /// </param>
        public static bool IsEnclosedIn(this string value, char c)
        {
            return value != null
                && value.Length > 1
                && value[0] == c
                && value[value.Length - 1] == c;
        }

        /// <summary>
        /// Determines whether the specified byte order is host (this computer
        /// architecture) byte order.
        /// </summary>
        /// <returns>
        /// <c>true</c> if <paramref name="order"/> is host byte order; otherwise,
        /// <c>false</c>.
        /// </returns>
        /// <param name="order">
        /// One of the <see cref="ByteOrder"/> enum values to test.
        /// </param>
        public static bool IsHostOrder(this ByteOrder order)
        {
            // true: !(true ^ true) or !(false ^ false)
            // false: !(true ^ false) or !(false ^ true)
            return !(BitConverter.IsLittleEndian ^ (order == ByteOrder.Little));
        }

        /// <summary>
        /// Determines whether the specified IP address is a local IP address.
        /// </summary>
        /// <remarks>
        /// This local means NOT REMOTE for the current host.
        /// </remarks>
        /// <returns>
        /// <c>true</c> if <paramref name="address"/> is a local IP address;
        /// otherwise, <c>false</c>.
        /// </returns>
        /// <param name="address">
        /// A <see cref="System.Net.IPAddress"/> to test.
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="address"/> is <see langword="null"/>.
        /// </exception>
        public static bool IsLocal(this System.Net.IPAddress address)
        {
            if (address == null)
                throw new ArgumentNullException(nameof(address));

            if (address.Equals(System.Net.IPAddress.Any))
                return true;

            if (address.Equals(System.Net.IPAddress.Loopback))
                return true;

            if (Socket.OSSupportsIPv6)
            {
                if (address.Equals(System.Net.IPAddress.IPv6Any))
                    return true;

                if (address.Equals(System.Net.IPAddress.IPv6Loopback))
                    return true;
            }

            string host = System.Net.Dns.GetHostName();
            var addrs = System.Net.Dns.GetHostAddresses(host);
            foreach (var addr in addrs)
            {
                if (address.Equals(addr))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Determines whether the specified string is <see langword="null"/> or
        /// an empty string.
        /// </summary>
        /// <returns>
        /// <c>true</c> if <paramref name="value"/> is <see langword="null"/> or
        /// an empty string; otherwise, <c>false</c>.
        /// </returns>
        /// <param name="value">
        /// A <see cref="string"/> to test.
        /// </param>
        public static bool IsNullOrEmpty(this string value)
        {
            return value == null || value.Length == 0;
        }

        /// <summary>
        /// Determines whether the specified string is a predefined scheme.
        /// </summary>
        /// <returns>
        /// <c>true</c> if <paramref name="value"/> is a predefined scheme;
        /// otherwise, <c>false</c>.
        /// </returns>
        /// <param name="value">
        /// A <see cref="string"/> to test.
        /// </param>
        public static bool IsPredefinedScheme(this ReadOnlySpan<Char> value)
        {
            if (value.Length < 2)
                return false;

            var sc = StringComparison.Ordinal;
            char c = value[0];
            if (c == 'h')
                return value.Equals("http".AsSpan(), sc) || value.Equals("https".AsSpan(), sc);

            if (c == 'w')
                return value.Equals("ws".AsSpan(), sc) || value.Equals("wss".AsSpan(), sc);

            if (c == 'f')
                return value.Equals("file".AsSpan(), sc) || value.Equals("ftp".AsSpan(), sc);

            if (c == 'g')
                return value.Equals("gopher".AsSpan(), sc);

            if (c == 'm')
                return value.Equals("mailto".AsSpan(), sc);

            if (c == 'n')
            {
                return value[1] == 'e' // second char
                    ? value.Equals("news".AsSpan(), sc) || value.Equals("net.pipe".AsSpan(), sc) || value.Equals("net.tcp".AsSpan(), sc)
                    : value.Equals("nntp".AsSpan(), sc);
            }

            return false;
        }

        /// <summary>
        /// Determines whether the specified string is a URI string.
        /// </summary>
        /// <returns>
        /// <c>true</c> if <paramref name="value"/> may be a URI string;
        /// otherwise, <c>false</c>.
        /// </returns>
        /// <param name="value">
        /// A <see cref="string"/> to test.
        /// </param>
        public static bool MaybeUri(this ReadOnlySpan<char> value)
        {
            if (value == null || value.Length == 0)
                return false;

            var idx = value.IndexOf(':');
            if (idx == -1)
                return false;

            if (idx >= 10)
                return false;

            var schm = value.Slice(0, idx);
            return schm.IsPredefinedScheme();
        }

        /// <summary>
        /// Retrieves a sub-array from the specified <paramref name="array"/>. A sub-array starts at
        /// the specified element position in <paramref name="array"/>.
        /// </summary>
        /// <returns>
        /// An array of T that receives a sub-array, or an empty array of T if any problems with
        /// the parameters.
        /// </returns>
        /// <param name="array">
        /// An array of T from which to retrieve a sub-array.
        /// </param>
        /// <param name="startIndex">
        /// A <see cref="long"/> that represents the zero-based starting position of
        /// a sub-array in <paramref name="array"/>.
        /// </param>
        /// <param name="length">
        /// A <see cref="long"/> that represents the number of elements to retrieve.
        /// </param>
        /// <typeparam name="T">
        /// The type of elements in <paramref name="array"/>.
        /// </typeparam>
        public static T[] SubArray<T>(this T[] array, long startIndex, long length)
        {
            long len;
            if (array == null || (len = array.LongLength) == 0)
                return Array.Empty<T>();

            if (startIndex < 0 || length <= 0 || startIndex + length > len)
                return Array.Empty<T>();

            if (startIndex == 0 && length == len)
                return array;

            var subArray = new T[length];
            Array.Copy(array, startIndex, subArray, 0, length);

            return subArray;
        }

        /// <summary>
        /// Converts the specified array of <see cref="byte"/> to the specified type data.
        /// </summary>
        /// <returns>
        /// A T converted from <paramref name="source"/>, or a default value of
        /// T if <paramref name="source"/> is an empty array of <see cref="byte"/> or
        /// if the type of T isn't <see cref="bool"/>, <see cref="char"/>, <see cref="double"/>,
        /// <see cref="float"/>, <see cref="int"/>, <see cref="long"/>, <see cref="short"/>,
        /// <see cref="uint"/>, <see cref="ulong"/>, or <see cref="ushort"/>.
        /// </returns>
        /// <param name="source">
        /// An array of <see cref="byte"/> to convert.
        /// </param>
        /// <param name="sourceOrder">
        /// One of the <see cref="ByteOrder"/> enum values, specifies the byte order of
        /// <paramref name="source"/>.
        /// </param>
        /// <typeparam name="T">
        /// The type of the return. The T must be a value type.
        /// </typeparam>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="source"/> is <see langword="null"/>.
        /// </exception>
        public static T To<T>(this byte[] source, ByteOrder sourceOrder)
          where T : unmanaged
        {
            if (source == null)
                throw new ArgumentNullException(nameof(source));

            if (source.Length == 0)
                return default;

            unsafe
            {
                int size = sizeof(T);
                if (source.Length < size)
                    throw new ArgumentException($"Not enough bytes for type ({typeof(T)}).", nameof(source));

                if (size > 0)
                {
                    byte[] buff = source.ToHostOrder(sourceOrder);
                    fixed (byte* ptr = buff)
                        return *((T*)ptr);
                }
                else
                    return default;
            }
        }

        /// <summary>
        /// Converts the specified <paramref name="value"/> to an array of <see cref="byte"/>.
        /// </summary>
        /// <param name="value">
        /// A T to convert.
        /// </param>
        /// <param name="order">
        /// One of the <see cref="ByteOrder"/> enum values, specifies the byte order of the return.
        /// </param>
        /// <typeparam name="T">
        /// The type of <paramref name="value"/>.
        /// </typeparam>
        /// <returns>
        /// An array of <see cref="byte"/> converted from <paramref name="value"/>.
        /// </returns>
        public static byte[] ToByteArray<T>(this T value, ByteOrder order)
            where T : unmanaged
        {
            unsafe
            {
                int size = sizeof(T);
                if (size > 0)
                {
                    byte[] array = new byte[size];
                    fixed (byte* ptr = array)
                        *((T*)ptr) = value;

                    if (!order.IsHostOrder())
                        Array.Reverse(array);
                    return array;
                }
                else
                    return Array.Empty<byte>();
            }
        }

        /// <summary>
        /// Converts the specified <paramref name="value"/> to an array of <see cref="byte"/>.
        /// </summary>
        /// <param name="value">
        /// A T to convert.
        /// </param>
        /// <param name="order">
        /// One of the <see cref="ByteOrder"/> enum values, specifies the byte order of the return.
        /// </param>
        /// <typeparam name="T">
        /// The type of <paramref name="value"/>.
        /// </typeparam>
        /// <returns>
        /// An array of <see cref="byte"/> converted from <paramref name="value"/>.
        /// </returns>
        public static Stream ToMemory<T>(this T value, ByteOrder order)
            where T : unmanaged
        {
            unsafe
            {
                int size = sizeof(T);
                if (size > 0)
                {
                    byte* tmp = stackalloc byte[size];
                    *((T*)tmp) = value;

                    var data = new Span<byte>(tmp, size);
                    if (!order.IsHostOrder())
                        data.Reverse();

                    var stream = RecyclableMemoryManager.Shared.GetStream();
                    stream.Write(data);
                    return stream;
                }
                else
                    return Stream.Null;
            }
        }

        public static void Write<T>(this Span<byte> dst, T value, ByteOrder order)
            where T : unmanaged
        {
            unsafe
            {
                int size = sizeof(T);
                if (dst.Length >= size)
                {
                    Span<T> ptr = MemoryMarshal.Cast<byte, T>(dst);
                    ptr[0] = value;

                    if (!order.IsHostOrder())
                        dst.Slice(0, size).Reverse();
                }
            }
        }

        /// <summary>
        /// Converts the order of elements in the specified byte array to
        /// host (this computer architecture) byte order.
        /// </summary>
        /// <returns>
        ///   <para>
        ///   An array of <see cref="byte"/> converted from
        ///   <paramref name="source"/>.
        ///   </para>
        ///   <para>
        ///   Or <paramref name="source"/> if the number of elements in it
        ///   is less than 2 or <paramref name="sourceOrder"/> is same as
        ///   host byte order.
        ///   </para>
        /// </returns>
        /// <param name="source">
        /// An array of <see cref="byte"/> to convert.
        /// </param>
        /// <param name="sourceOrder">
        ///   <para>
        ///   One of the <see cref="ByteOrder"/> enum values.
        ///   </para>
        ///   <para>
        ///   It specifies the order of elements in <paramref name="source"/>.
        ///   </para>
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="source"/> is <see langword="null"/>.
        /// </exception>
        public static byte[] ToHostOrder(this byte[] source, ByteOrder sourceOrder)
        {
            if (source == null)
                throw new ArgumentNullException(nameof(source));

            if (source.Length < 2)
                return source;

            return !sourceOrder.IsHostOrder() ? source.Reverse() : source;
        }

        /// <summary>
        /// Converts the specified array to a <see cref="string"/>.
        /// </summary>
        /// <returns>
        ///   <para>
        ///   A <see cref="string"/> converted by concatenating each element of
        ///   <paramref name="array"/> across <paramref name="separator"/>.
        ///   </para>
        ///   <para>
        ///   An empty string if <paramref name="array"/> is an empty array.
        ///   </para>
        /// </returns>
        /// <param name="array">
        /// An array of T to convert.
        /// </param>
        /// <param name="separator">
        /// A <see cref="string"/> used to separate each element of
        /// <paramref name="array"/>.
        /// </param>
        /// <typeparam name="T">
        /// The type of elements in <paramref name="array"/>.
        /// </typeparam>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="array"/> is <see langword="null"/>.
        /// </exception>
        public static string ToString<T>(this T[] array, string separator)
        {
            if (array == null)
                throw new ArgumentNullException(nameof(array));

            int len = array.Length;
            if (len == 0)
                return string.Empty;

            if (separator == null)
                separator = string.Empty;

            var buff = new StringBuilder(64);
            for (var i = 0; i < len - 1; i++)
                buff.AppendFormat("{0}{1}", array[i], separator);

            buff.Append(array[len - 1].ToString());
            return buff.ToString();
        }

        public static int ParseInt32(this ReadOnlySpan<char> span)
        {
            // TODO: reduce allocs
            return int.Parse(span.ToString());
        }

        /// <summary>
        /// Converts the specified string to a <see cref="Uri"/>.
        /// </summary>
        /// <returns>
        ///   <para>
        ///   A <see cref="Uri"/> converted from <paramref name="value"/>.
        ///   </para>
        ///   <para>
        ///   <see langword="null"/> if the conversion has failed.
        ///   </para>
        /// </returns>
        /// <param name="value">
        /// A <see cref="string"/> to convert.
        /// </param>
        public static Uri ToUri(this string value)
        {
            Uri.TryCreate(value, value.AsSpan().MaybeUri() ? UriKind.Absolute : UriKind.Relative, out Uri ret);
            return ret;
        }
        /// <summary>
        /// Converts the specified string to a <see cref="Uri"/>.
        /// </summary>
        /// <returns>
        ///   <para>A <see cref="Uri"/> converted from <paramref name="value"/>.</para>
        ///   <para><see langword="null"/> if the conversion has failed.</para>
        /// </returns>
        /// <param name="span">A <see cref="ReadOnlySpan{char}"/> to convert.</param>
        public static Uri ToUri(this ReadOnlySpan<char> span)
        {
            // TODO: reduce allocs
            return ToUri(span.ToString());
        }

        /// <summary>
        /// Writes and sends the specified <paramref name="content"/> data with the specified
        /// <see cref="HttpListenerResponse"/>.
        /// </summary>
        /// <param name="response">
        /// A <see cref="HttpListenerResponse"/> that represents the HTTP response used to
        /// send the content data.
        /// </param>
        /// <param name="content">
        /// An array of <see cref="byte"/> that represents the content data to send.
        /// </param>
        /// <exception cref="ArgumentNullException">
        ///   <para>
        ///   <paramref name="response"/> is <see langword="null"/>.
        ///   </para>
        ///   <para>
        ///   -or-
        ///   </para>
        ///   <para>
        ///   <paramref name="content"/> is <see langword="null"/>.
        ///   </para>
        /// </exception>
        public static void Send(this HttpListenerResponse response, byte[] content)
        {
            if (response == null)
                throw new ArgumentNullException(nameof(response));

            if (content == null)
                throw new ArgumentNullException(nameof(content));

            int len = content.Length;
            if (len == 0)
            {
                response.Close();
                return;
            }

            response.ContentLength64 = len;
            var output = response.OutputStream;

            output.Write(content, 0, len);
            output.Close();
        }

        #endregion
    }
}