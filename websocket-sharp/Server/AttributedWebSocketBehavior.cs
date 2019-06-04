using System;
using System.Collections.Generic;
using System.IO;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using WebSocketSharp.Memory;

namespace WebSocketSharp.Server
{
    public abstract class AttributedWebSocketBehavior : WebSocketBehavior
    {
        public delegate void HandlerDelegate(AttributedWebSocketBehavior behavior, JToken body);

        private static Dictionary<Type, HandlerCollection> _handlerCache;
        private static JsonSerializer _defaultSerializer;

        private HandlerCollection _handlers;

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
                    _handlers = CreateMessageHandlers(type);
                    _handlerCache.Add(type, _handlers);
                }
            }
        }

        public static HandlerCollection CreateMessageHandlers(Type type)
        {
            // TODO: consider finding public properties and returning
            // a serialized version of the value returned by the getter

            var collection = new HandlerCollection();
            var methods = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            foreach (var method in methods)
            {
                if (method.IsSpecialName)
                    continue;

                var attribs = method.GetCustomAttributes(typeof(MessageHandlerAttribute), false);
                if (attribs.Length != 1)
                    continue;

                var attrib = attribs[0] as MessageHandlerAttribute;
                string name = attrib.Name ?? method.Name;

                var targetBase = Expression.Parameter(typeof(AttributedWebSocketBehavior), "target");
                var message = Expression.Parameter(typeof(JToken), "message");
                var target = Expression.Convert(targetBase, type);
                var call = Expression.Call(target, method, message);

                // lambda visualized: (target, message) => ((type)target).Invoke(message)
                var handler = Expression.Lambda<HandlerDelegate>(call, targetBase, message).Compile();

                collection.Add(name, handler);
                if (attrib.Code != -1)
                    collection.Add(attrib.Code, handler);
            }
            return collection;
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
                    var named = CaseSensitiveCodes ? _handlers.Named : _handlers.NamedIgnoreCase;
                    string code = codeToken.ToObject<string>();
                    if (named.TryGetValue(code, out var handler))
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
                    if (_handlers.Coded.TryGetValue(code, out var handler))
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

        protected void SendAsJson(string key, object body, JsonSerializer serializer)
        {
            AssertOpen();

            using (var tmp = RecyclableMemoryManager.Shared.GetStream())
            using (var writer = new StreamWriter(tmp, Encoding.UTF8, 1024))
            {
                var json = new JsonTextWriter(writer);
                json.WriteStartArray();
                json.WriteValue(key);
                serializer.Serialize(json, body);
                json.WriteEndArray();
                json.Flush();

                tmp.Position = 0;
                Send(MessageFrameType.Text, tmp, (int)tmp.Length);
            }
        }

        protected void SendAsJson(string key, object body)
        {
            SendAsJson(key, body, _defaultSerializer);
        }

        protected void SendError(string message)
        {
            SendAsJson("error", message);
        }

        public class HandlerCollection
        {
            public Dictionary<int, HandlerDelegate> Coded { get; }
            public Dictionary<string, HandlerDelegate> Named { get; }
            public Dictionary<string, HandlerDelegate> NamedIgnoreCase { get; }

            public HandlerCollection()
            {
                Coded = new Dictionary<int, HandlerDelegate>();
                Named = new Dictionary<string, HandlerDelegate>(StringComparer.Ordinal);
                NamedIgnoreCase = new Dictionary<string, HandlerDelegate>(StringComparer.OrdinalIgnoreCase);
            }

            public void Add(int code, HandlerDelegate handler)
            {
                Coded.Add(code, handler);
            }

            public void Add(string code, HandlerDelegate handler)
            {
                if (NamedIgnoreCase.ContainsKey(code))
                    throw new ArgumentException();
                Named.Add(code, handler);
                NamedIgnoreCase.Add(code, handler);
            }
        }
    }
}
