using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Reflection;

namespace WebSocketSharp.Server
{
    public class HandlerCollection
    {
        public delegate void HandlerDelegate(AttributedWebSocketBehavior behavior, JToken body);

        public Dictionary<int, HandlerDelegate> ByCode { get; }
        public Dictionary<string, HandlerDelegate> ByName { get; }
        public Dictionary<string, HandlerDelegate> ByNameIgnoreCase { get; }

        public HandlerCollection()
        {
            ByCode = new Dictionary<int, HandlerDelegate>();
            ByName = new Dictionary<string, HandlerDelegate>(StringComparer.Ordinal);
            ByNameIgnoreCase = new Dictionary<string, HandlerDelegate>(StringComparer.OrdinalIgnoreCase);
        }

        public void Add(int code, HandlerDelegate handler)
        {
            ByCode.Add(code, handler);
        }

        public void Add(string code, HandlerDelegate handler)
        {
            if (ByNameIgnoreCase.ContainsKey(code))
                throw new ArgumentException();

            ByNameIgnoreCase.Add(code, handler);
            ByName.Add(code, handler);
        }

        public static HandlerCollection Create(Type behaviorType)
        {
            // TODO: consider finding public properties and returning
            // a serialized version of the value returned by the getter

            if (!typeof(AttributedWebSocketBehavior).IsAssignableFrom(behaviorType))
                throw new ArgumentException(
                    $"Type must inherit from {nameof(AttributedWebSocketBehavior)}.");

            var collection = new HandlerCollection();
            var methods = behaviorType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
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
                var target = Expression.Convert(targetBase, behaviorType);
                var call = Expression.Call(target, method, message);

                // lambda visualized: (target, message) => ((behaviorType)target).Invoke(message)
                var handler = Expression.Lambda<HandlerDelegate>(call, targetBase, message).Compile();

                collection.Add(name, handler);
                if (attrib.Code != -1)
                    collection.Add(attrib.Code, handler);
            }
            return collection;
        }

        public static HandlerCollection Create<TBehavior>()
            where TBehavior : AttributedWebSocketBehavior
        {
            return Create(typeof(TBehavior));
        }
    }
}
