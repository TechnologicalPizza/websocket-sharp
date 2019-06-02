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

        private static Dictionary<Type, Dictionary<string, HandlerDelegate>> _handlerCache;
        private static Dictionary<Type, Dictionary<string, HandlerDelegate>> _caseInsensitiveHandlerCache;
        private static JsonSerializer _defaultSerializer;

        private Dictionary<string, HandlerDelegate> _handlers;

        public bool CaseSensitiveCodes { get; }
        public JsonLoadSettings LoadSettings { get; }

        static AttributedWebSocketBehavior()
        {
            _handlerCache = new Dictionary<Type, Dictionary<string, HandlerDelegate>>();
            _caseInsensitiveHandlerCache = new Dictionary<Type, Dictionary<string, HandlerDelegate>>();

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
                    _handlers = CreateMessageHandlers(type, StringComparer.Ordinal);
                    _handlerCache.Add(type, _handlers);
                }

                if (!CaseSensitiveCodes)
                {
                    if (!_caseInsensitiveHandlerCache.TryGetValue(type, out _handlers))
                    {
                        _handlers = CreateMessageHandlers(type, StringComparer.OrdinalIgnoreCase);
                        _caseInsensitiveHandlerCache.Add(type, _handlers);
                    }
                }
            }
        }

        public static Dictionary<string, HandlerDelegate> CreateMessageHandlers(
            Type type, IEqualityComparer<string> comparer)
        {
            // TODO: consider finding public properties and returning
            // a serialized version of the value returned by the getter

            var handlers = new Dictionary<string, HandlerDelegate>(comparer);
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
                handlers.Add(name, handler);
            }
            return handlers;
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
                var bs = reader.BaseStream;
                bs.Position = 0;
                bs.Write(e.RawData.Span);
                bs.SetLength(e.RawData.Length);

                bs.Position = 0;
                reader.DiscardBufferedData();

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
            if (codeToken.Type != JTokenType.String)
            {
                SendError("Message code must be of type String.");
                return;
            }

            var bodyToken = obj[1];
            if (bodyToken == null)
            {
                SendError($"Message body missing.");
                return;
            }

            string code = codeToken.ToObject<string>();
            if (_handlers.TryGetValue(code, out var handler))
            {
                handler.Invoke(this, bodyToken);
            }
            else
            {
                SendError($"Unknown message code '{code}'");
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
    }
}
