using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Reflection;

namespace WebSocketSharp.Server
{
    public abstract partial class AttributedWebSocketBehavior
    {
        public class HandlerCollection
        {
            public delegate void HandlerDelegate(AttributedWebSocketBehavior behavior, JToken body);

            public Dictionary<int, HandlerDelegate> ByCode { get; }
            public Dictionary<string, HandlerDelegate> ByName { get; }

            public HandlerCollection()
            {
                ByCode = new Dictionary<int, HandlerDelegate>();
                ByName = new Dictionary<string, HandlerDelegate>(StringComparer.OrdinalIgnoreCase);
            }

            public void Add(int code, HandlerDelegate handler)
            {
                ByCode.Add(code, handler);
            }

            public void Add(string code, HandlerDelegate handler)
            {
                if (ByName.ContainsKey(code))
                    throw new ArgumentException(
                        "There is already a handler whose name only differs in casing.");
                ByName.Add(code.ToLower(), handler);
            }

            public static HandlerCollection Create(Type behaviorType, CodeCollection codes = null)
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
                    {
                        collection.Add(attrib.Code, handler);
                    }
                    else if (codes != null)
                    {
                        if(codes.TryGetCode(name, out int code))
                            collection.Add(code, handler);
                    }
                }
                return collection;
            }
        }
    }
}