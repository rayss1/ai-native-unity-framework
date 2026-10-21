namespace AiNative.Realtime
{
    // Frozen wire layout of the pinned Fantasy transport. No vendor runtime dependency.
    // Bits 0..22: application index; 23..26: serializer; 27..31: message category.
    internal static class ControlOpCodeLayout
    {
        internal const int SerializerShift = 23;
        internal const int MessageCategoryShift = 27;
        internal const uint Protobuf = 0;
        internal const uint GateControlIndex = 177;
        internal const uint ServiceControlIndex = 177;
        internal const uint ExternalMessage = 1;
        internal const uint ExternalRequest = 2;
        internal const uint ExternalResponse = 3;
        internal const uint InternalAddressRequest = 8;
        internal const uint InternalAddressResponse = 9;
    }

    /// <summary>Client/Gate control envelopes. Business operations are selected by method inside the payload.</summary>
    public static class GateTransportOpCodes
    {
        public const uint Request =
            (ControlOpCodeLayout.ExternalRequest << ControlOpCodeLayout.MessageCategoryShift)
            | (ControlOpCodeLayout.Protobuf << ControlOpCodeLayout.SerializerShift)
            | ControlOpCodeLayout.GateControlIndex;
        public const uint Response =
            (ControlOpCodeLayout.ExternalResponse << ControlOpCodeLayout.MessageCategoryShift)
            | (ControlOpCodeLayout.Protobuf << ControlOpCodeLayout.SerializerShift)
            | ControlOpCodeLayout.GateControlIndex;
        public const uint Push =
            (ControlOpCodeLayout.ExternalMessage << ControlOpCodeLayout.MessageCategoryShift)
            | (ControlOpCodeLayout.Protobuf << ControlOpCodeLayout.SerializerShift)
            | ControlOpCodeLayout.GateControlIndex;
    }

    /// <summary>Address-routed service RPC envelopes, distinct from client/Gate requests.</summary>
    public static class ServiceControlTransportOpCodes
    {
        public const uint Request =
            (ControlOpCodeLayout.InternalAddressRequest << ControlOpCodeLayout.MessageCategoryShift)
            | (ControlOpCodeLayout.Protobuf << ControlOpCodeLayout.SerializerShift)
            | ControlOpCodeLayout.ServiceControlIndex;
        public const uint Response =
            (
                ControlOpCodeLayout.InternalAddressResponse
                << ControlOpCodeLayout.MessageCategoryShift
            )
            | (ControlOpCodeLayout.Protobuf << ControlOpCodeLayout.SerializerShift)
            | ControlOpCodeLayout.ServiceControlIndex;
    }
}
