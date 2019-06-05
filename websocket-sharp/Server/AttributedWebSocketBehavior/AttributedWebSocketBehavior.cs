using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using WebSocketSharp.Memory;

namespace WebSocketSharp.Server
{
    public abstract class AttributedWebSocketBehavior : WebSocketBehavior
    {
        private static Dictionary<Type, HandlerCollection> _handlerCache;
        private static JsonSerializer _defaultSerializer;

        private HandlerCollection _handlers;
        private CodeCollection _codes;

        public bool CaseSensitiveCodes { get; }
        public JsonLoadSettings LoadSettings { get; }

        static AttributedWebSocketBehavior()
        {
            _handlerCache = new Dictionary<Type, HandlerCollection>();

            _defaultSerializer = JsonSerializer.Create(new JsonSerializerSettings
            {
                NullValueHandling = NullValueHandling.Include,
                Formatting = Formatting.None
            });
        }

        public AttributedWebSocketBehavior(bool caseSensitiveCodes = true, JsonLoadSettings loadSettings = null)
        {
            CaseSensitiveCodes = caseSensitiveCodes;
            LoadSettings = loadSettings ?? new JsonLoadSettings
            {
                CommentHandling = CommentHandling.Ignore,
                LineInfoHandling = LineInfoHandling.Ignore,
                DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Replace
            };

            lock (_handlerCache)
            {
                Type type = GetType();
                if (!_handlerCache.TryGetValue(type, out _handlers))
                {
                    _handlers = HandlerCollection.Create(type);
                    _handlerCache.Add(type, _handlers);
                }
            }
        }

        protected bool Missing(JToken token, string name, out JToken value)
        {
            if (token == null || (value = token[name]) == null)
            {
                Send("Missing property '" + name + "'.");
                value = null;
                return true;
            }
            return false;
        }

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
                SendError(ex.Message);
                return;
            }
            finally
            {
                RecyclableMemoryManager.Shared.ReturnReader(reader);
            }

            var codeToken = obj[0];
            if (codeToken == null)
            {
                SendError($"Message code missing.");
                return;
            }

            var bodyToken = obj[1];
            if (bodyToken == null)
            {
                SendError($"Message body missing.");
                return;
            }

            void SendError_UnknownCode(string code) => SendError($"Unknown message code '{code}'");
        
            switch (codeToken.Type)
            {
                case JTokenType.String:
                {
                    var handlers = CaseSensitiveCodes ? _handlers.ByName : _handlers.ByNameIgnoreCase;
                    string code = codeToken.ToObject<string>();
                    if (handlers.TryGetValue(code, out var handler))
                    {
                        handler.Invoke(this, bodyToken);
                        break;
                    }
                    SendError_UnknownCode(code);
                    break;
                }

                case JTokenType.Integer:
                {
                    int code = codeToken.ToObject<int>();
                    if(code == -1)
                    {
                        SendError("Message code '-1' is reserved.");
                        break;
                    }
                    if (_handlers.ByCode.TryGetValue(code, out var handler))
                    {
                        handler.Invoke(this, bodyToken);
                        break;
                    }
                    SendError_UnknownCode(code.ToString());
                    break;
                }

                default:
                    SendError($"Message code must be of type String or Integer.");
                    break;
            }
        }

        private JsonTextWriter GetJsonMessageWriter(out StreamWriter writer)
        {
            AssertOpen();
            var tmp = RecyclableMemoryManager.Shared.GetStream();
            writer = new StreamWriter(tmp, Encoding.UTF8, 1024)
            {
                AutoFlush = false
            };

            var json = new JsonTextWriter(writer);
            json.WriteStartArray();
            return json;
        }

        private void FinishJsonMessage(JsonTextWriter json, StreamWriter writer)
        {
            json.WriteEndArray();
            json.Flush();

            var stream = writer.BaseStream;
            stream.Position = 0;
            Send(MessageFrameType.Text, stream, (int)stream.Length);
        }

        protected void SendAsJson(string key, object body, JsonSerializer serializer)
        {
            using (var json = GetJsonMessageWriter(out var stream))
            {
                json.WriteValue(key);
                serializer.Serialize(json, body);
                FinishJsonMessage(json, stream);
            }
        }

        protected void SendAsJson(int key, object body, JsonSerializer serializer)
        {
            using (var json = GetJsonMessageWriter(out var stream))
            {
                json.WriteValue(key);
                serializer.Serialize(json, body);
                FinishJsonMessage(json, stream);
            }
        }

        protected void SendAsJson(string key, object body)
        {
            SendAsJson(key, body, _defaultSerializer);
        }

        protected void SendAsJson(int key, object body)
        {
            SendAsJson(key, body, _defaultSerializer);
        }

        protected void SendError(string message)
        {
            SendAsJson("error", message);
        }
    }
}
