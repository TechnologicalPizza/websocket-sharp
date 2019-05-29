using System;

namespace WebSocketSharp.Server
{
    [AttributeUsage(AttributeTargets.Method, Inherited = false)]
    public sealed class MessageHandlerAttribute : Attribute
    {
        public string Name { get; }

        public MessageHandlerAttribute(string name = null)
        {
            Name = name;
        }
    }
}
