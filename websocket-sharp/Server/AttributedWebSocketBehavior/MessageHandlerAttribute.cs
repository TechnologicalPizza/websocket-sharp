using System;

namespace WebSocketSharp.Server
{
    [AttributeUsage(AttributeTargets.Method, Inherited = false)]
    public sealed class MessageHandlerAttribute : Attribute
    {
        public int Code { get; set; }
        public string Name { get; set; }

        public MessageHandlerAttribute()
        {
            Code = -1;
        }

        public MessageHandlerAttribute(int code)
        {
            Code = code;
        }

        public MessageHandlerAttribute(string name)
        {
            Name = name;
        }
    }
}
