using System;
using System.Diagnostics;

namespace WebSocketSharp.Server
{
    public abstract partial class AttributedWebSocketBehavior
    {
        public const string InvalidEnumError = "The type is not an Int32 enum.";

        public readonly struct CodeEnumDefinition
        {
            public Type Client { get; }
            public Type Server { get; }

            public bool IsValid => Client != null && Server != null;

            public CodeEnumDefinition(Type clientCodes, Type serverCodes)
            {
                AssertValidType(clientCodes, nameof(clientCodes));
                AssertValidType(serverCodes, nameof(serverCodes));

                Client = clientCodes;
                Server = serverCodes;
            }

            [DebuggerHidden]
            private static void AssertValidType(Type enumType, string argName)
            {
                if (enumType == null)
                    throw new ArgumentNullException(argName);

                if (!IsValidEnum(enumType))
                    throw new ArgumentException(InvalidEnumError, argName);
            }

            public static bool IsValidEnum(Type enumType)
            {
                return enumType.IsEnum
                    && enumType.GetEnumUnderlyingType() == typeof(int);
            }
        }
    }
}
