using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using WebSocketSharp.Memory;

namespace WebSocketSharp.Server
{
    public abstract partial class AttributedWebSocketBehavior : WebSocketBehavior
    {
        private static Dictionary<Type, HandlerCollection> _handlerCache;
        private static Dictionary<Type, CodeCollection> _clientCodeCache;
        private static Dictionary<Type, CodeCollection> _serverCodeCache;

        private HandlerCollection _handlers;
        private CodeCollection _clientCodes;
        private CodeCollection _serverCodes;

        public bool VerboseDebug { get; set; }
        public bool UseNumericCodes { get; }
        public JsonLoadSettings LoadSettings { get; }

        #region Constructors

        static AttributedWebSocketBehavior()
        {
            _handlerCache = new Dictionary<Type, HandlerCollection>();
            _clientCodeCache = new Dictionary<Type, CodeCollection>();
            _serverCodeCache = new Dictionary<Type, CodeCollection>();
        }

        public AttributedWebSocketBehavior(
            JsonLoadSettings loadSettings = null,
            CodeEnumDefinition codeTypeDefinition = default)
        {
            LoadSettings = loadSettings ?? new JsonLoadSettings
            {
                CommentHandling = CommentHandling.Ignore,
                LineInfoHandling = LineInfoHandling.Ignore,
                DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Replace
            };
            UseNumericCodes = codeTypeDefinition.IsValid;

            if (UseNumericCodes)
                InitCodes(codeTypeDefinition);
            InitHandlers();
        }

        #endregion

        #region Initializing

        private void InitHandlers()
        {
            lock (_handlerCache)
            {
                Type type = GetType();
                if (!_handlerCache.TryGetValue(type, out _handlers))
                {
                    _handlers = HandlerCollection.Create(type, _clientCodes);
                    _handlerCache.Add(type, _handlers);
                }
            }
        }

        private void InitCodes(CodeEnumDefinition typeDefinition)
        {
            lock (_clientCodeCache)
            {
                if (!_clientCodeCache.TryGetValue(typeDefinition.Client, out _clientCodes))
                {
                    _clientCodes = new CodeCollection(typeDefinition.Client);
                    _clientCodeCache.Add(typeDefinition.Client, _clientCodes);
                }
            }

            lock (_serverCodeCache)
            {
                if (!_serverCodeCache.TryGetValue(typeDefinition.Server, out _serverCodes))
                {
                    _serverCodes = new CodeCollection(typeDefinition.Server);
                    _serverCodeCache.Add(typeDefinition.Server, _serverCodes);
                }
            }
        }

        #endregion

        /// <summary>
        /// Called when the WebSocket connection for a session has been established.
        /// Sends an initialization message to the client.
        /// </summary>
        protected override void OnOpen()
        {
            SendInitMessage();
        }

        #region Init Message

        private void SendInitMessage()
        {
            object codeInfo = GetCodeInfo();
            SendJson(new { codeInfo });
        }

        private object GetCodeInfo()
        {
            if (UseNumericCodes)
            {
                return new
                {
                    numericCodes = true,
                    client = _clientCodes?.ByName,
                    server = _serverCodes?.ByName
                };
            }
            else
            {
                return new
                {
                    numericCodes = false,
                    client = _handlers.ByName.Keys,
                };
            }
        }

        #endregion

        #region 'Missing' Helper

        /// <summary>
        /// Helper for checking if the <see cref="JToken"/> has the specified property, 
        /// sending an error if the property is missing.
        /// </summary>
        /// <param name="token">The token to check.</param>
        /// <param name="name">The name of the property.</param>
        /// <param name="value">The found value in the token.</param>
        /// <returns></returns>
        protected bool Missing(JToken token, string name, out JToken value)
        {
            if (token == null || (value = token[name]) == null)
            {
                SendError(token, "Missing property '" + name + "'.");
                value = null;
                return true;
            }
            return false;
        }

        /// <summary>
        /// Helper for checking if the <see cref="JArray"/> has an element at
        /// the specified index, sending an error if the property is missing.
        /// </summary>
        /// <param name="array"></param>
        /// <param name="name">The name of the property.</param>
        /// <param name="value">The found value in the token.</param>
        /// <returns></returns>
        protected bool Missing(JArray array, int index, out JToken value)
        {
            if (array == null || (value = array[index]) == null)
            {
                SendError(array, "Missing value at index '" + index + "'.");
                value = null;
                return true;
            }
            return false;
        }

        #endregion

        #region OnMessage and Send

        /// <summary>
        /// Called when the WebSocket instance for a session receives a message.
        /// </summary>
        /// <param name="e">
        /// A <see cref="MessageEvent"/> that represents the event data passed
        /// from a <see cref="WebSocket.OnMessage"/> event.
        /// </param>
        protected override void OnMessage(MessageEvent e)
        {
            if (e.IsPing)
                return;

            if (!e.IsText)
            {
                SendError("Only text messages are supported.");
                return;
            }

            JArray obj;
            var reader = RecyclableMemoryManager.Shared.GetReader(e.RawData.Span);
            try
            {
                using (var json = new JsonTextReader(reader) { CloseInput = false })
                    obj = JArray.Load(json, LoadSettings);
            }
            catch (Exception ex)
            {
                reader.DiscardBufferedData();
                reader.BaseStream.Position = 0;

                SendError(reader.ReadToEnd(), ex.Message);
                return;
            }
            finally
            {
                RecyclableMemoryManager.Shared.ReturnReader(reader);
            }

            if (obj == null) // this should never happen
            {
                SendError("Message was parsed as null.");
                return;
            }

            var codeToken = obj.Count > 0 ? obj[0] : null;
            if (codeToken == null || codeToken.Type == JTokenType.Null)
            {
                SendError(obj, "Message code missing.");
                return;
            }

            var bodyToken = obj.Count > 1 ? obj[1] : null;
            if (bodyToken == null || bodyToken.Type == JTokenType.Null)
            {
                SendError(obj, "Message body missing.");
                return;
            }

            void SendError_UnknownCode() =>
                SendError(obj, $"Message code '{codeToken}' is unknown.");
        
            switch (codeToken.Type)
            {
                case JTokenType.String:
                {
                    string code = codeToken.ToObject<string>();
                    if (_handlers.ByName.TryGetValue(code, out var handler))
                    {
                        handler.Invoke(this, bodyToken);
                        break;
                    }
                    SendError_UnknownCode();
                    break;
                }

                case JTokenType.Integer:
                {
                    int code = codeToken.ToObject<int>();
                    if(code == -1)
                    {
                        SendError(obj, $"Message code '{codeToken}' is reserved.");
                        break;
                    }
                    if (_handlers.ByCode.TryGetValue(code, out var handler))
                    {
                        handler.Invoke(this, bodyToken);
                        break;
                    }
                    SendError_UnknownCode();
                    break;
                }

                default:
                    SendError(obj, "Message code must be of type String or Integer.");
                    break;
            }
        }

        protected void SendJson(string key, object body, JsonSerializer serializer)
        {
            using (var json = GetJsonMessageWriter(out var stream))
            {
                json.WriteStartArray();
                json.WriteValue(key);
                if (body != null)
                    serializer.Serialize(json, body);
                json.WriteEndArray();

                FinishJsonMessage(json, stream);
            }
        }

        protected void SendJson(string key, object body)
        {
            SendJson(key, body, DefaultSerializer);
        }

        protected void SendJson(int key, object body, JsonSerializer serializer)
        {
            using (var json = GetJsonMessageWriter(out var stream))
            {
                json.WriteStartArray();
                json.WriteValue(key);
                if(body != null)
                    serializer.Serialize(json, body);
                json.WriteEndArray();

                FinishJsonMessage(json, stream);
            }
        }

        protected void SendJson(int key, object body)
        {
            SendJson(key, body, DefaultSerializer);
        }

        protected void SendError(object message)
        {
            SendJson(-1, message);
        }

        protected void SendError(object source, object message)
        {
            if (VerboseDebug)
                SendError(new { source, message });
            else
                SendError(message);
        }

        #endregion
    }
}
