#region License
/*
 * HttpRequestEventArgs.cs
 *
 * The MIT License
 *
 * Copyright (c) 2012-2017 sta.blockhead
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

using System;
using System.IO;
using System.Security.Principal;
using WebSocketSharp.Net;

namespace WebSocketSharp.Server
{
    /// <summary>
    /// Represents the event data for the HTTP request events of
    /// the <see cref="HttpServer"/>.
    /// </summary>
    /// <remarks>
    ///   <para>
    ///   An HTTP request event occurs when the <see cref="HttpServer"/>
    ///   receives an HTTP request.
    ///   </para>
    ///   <para>
    ///   You should access the <see cref="Request"/> property if you would
    ///   like to get the request data sent from a client.
    ///   </para>
    ///   <para>
    ///   And you should access the <see cref="Response"/> property if you would
    ///   like to get the response data to return to the client.
    ///   </para>
    /// </remarks>
    public readonly struct HttpRequestEvent
    {
        #region Private Fields

        private readonly HttpListenerContext _context;
        private readonly string _docRootPath;

        #endregion

        #region Internal Constructors

        internal HttpRequestEvent(
            HttpListenerContext context, string documentRootPath)
        {
            _context = context;
            _docRootPath = documentRootPath;
        }

        #endregion

        #region Public Properties

        /// <summary>
        /// Gets the request data sent from a client.
        /// </summary>
        /// <value>
        /// A <see cref="HttpListenerRequest"/> that provides the methods and
        /// properties for the request data.
        /// </value>
        public HttpListenerRequest Request => _context.Request;

        /// <summary>
        /// Gets the response data to return to the client.
        /// </summary>
        /// <value>
        /// A <see cref="HttpListenerResponse"/> that provides the methods and
        /// properties for the response data.
        /// </value>
        public HttpListenerResponse Response => _context.Response;

        /// <summary>
        /// Gets the information for the client.
        /// </summary>
        /// <value>
        ///   <para>
        ///   A <see cref="IPrincipal"/> instance or <see langword="null"/>
        ///   if not authenticated.
        ///   </para>
        ///   <para>
        ///   That instance describes the identity, authentication scheme,
        ///   and security roles for the client.
        ///   </para>
        /// </value>
        public IPrincipal User => _context.User;

        #endregion

        #region Public Methods

        /// <summary>
        /// Reads the specified file from the document folder of
        /// the <see cref="HttpServer"/>.
        /// </summary>
        /// <returns>
        ///   <para>
        ///   An array of <see cref="byte"/> or <see langword="null"/>
        ///   if it fails.
        ///   </para>
        ///   <para>
        ///   That array receives the contents of the file.
        ///   </para>
        /// </returns>
        /// <param name="path">
        /// A <see cref="string"/> that represents a virtual path to
        /// find the file from the document folder.
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="path"/> is <see langword="null"/>.
        /// </exception>
        /// <exception cref="ArgumentException">
        ///   <para>
        ///   <paramref name="path"/> is an empty string.
        ///   </para>
        ///   <para>
        ///   -or-
        ///   </para>
        ///   <para>
        ///   <paramref name="path"/> contains "..".
        ///   </para>
        /// </exception>
        public byte[] ReadFile(string path)
        {
            TryReadFile(path, out byte[] contents);
            return contents;
        }

        /// <summary>
        /// Tries to read the specified file from the document folder of
        /// the <see cref="HttpServer"/>.
        /// </summary>
        /// <returns>
        /// <c>true</c> if it succeeds to read; otherwise, <c>false</c>.
        /// </returns>
        /// <param name="path">
        /// A <see cref="string"/> that represents a virtual path to
        /// find the file from the document folder.
        /// </param>
        /// <param name="contents">
        ///   <para>
        ///   When this method returns, an array of <see cref="byte"/> or
        ///   <see langword="null"/> if it fails.
        ///   </para>
        ///   <para>
        ///   That array receives the contents of the file.
        ///   </para>
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="path"/> is <see langword="null"/>.
        /// </exception>
        /// <exception cref="ArgumentException">
        ///   <para>
        ///   <paramref name="path"/> is an empty string.
        ///   </para>
        ///   <para>
        ///   -or-
        ///   </para>
        ///   <para>
        ///   <paramref name="path"/> contains "..".
        ///   </para>
        /// </exception>
        public bool TryReadFile(string path, out byte[] contents)
        {
            if (path == null)
                throw new ArgumentNullException(nameof(path));

            if (path.Length == 0)
                throw new ArgumentException("An empty string.", nameof(path));

            if (path.IndexOf("..") > -1)
                throw new ArgumentException("It contains '..'.", nameof(path));

            path = path.TrimStart('/', '\\');
            path = $"{_docRootPath}/{path}".Replace('\\', '/');

            if (!File.Exists(path))
            {
                contents = null;
                return false;
            }

            try
            {
                contents = File.ReadAllBytes(path);
                return true;
            }
            catch
            {
                contents = null;
                return false;
            }
        }

        #endregion
    }
}