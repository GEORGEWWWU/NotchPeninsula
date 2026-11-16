using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Windows.Foundation;
using Windows.Graphics.Effects;
using WinRT;
using WinRT.Interop;

namespace NotchPeninsula;

// Descriptor for the built-in Direct2D Gaussian blur effect. Composition owns the backdrop
// and applies the blur; the app only supplies the standard deviation and source name.
[WinRTExposedType(typeof(GlassBlurEffect.InterfaceDetails))]
internal sealed class GlassBlurEffect(IGraphicsEffectSource source) : IGraphicsEffect, IGraphicsEffectSource
{
    internal const string SourceName = "Backdrop";
    internal const string AmountProperty = "Blur.BlurAmount";
    public string Name { get; set; } = "Blur";
    private IGraphicsEffectSource Source { get; } = source;

    public sealed unsafe class InterfaceDetails : IWinRTExposedTypeDetails
    {
        private const int InvalidArgument = unchecked((int)0x80070057);
        private static readonly Guid GaussianBlur = new("1FEB6D69-2FE6-4AC9-8C58-1D7F93E7A6A5");
        private static readonly Guid PropertyValueInterface = new("4BD682DD-7554-40E9-9A9B-82654EDE7E62");
        private static readonly ComWrappers.ComInterfaceEntry[] Interfaces =
        [
            new() { IID = new("CB51C0CE-8FE6-4636-B202-861FAA07D8F3"), Vtable = CreateGraphicsVtable() },
            new() { IID = new("2D8F9DDC-4339-4EB9-9216-F9DEB75658A2"), Vtable = IInspectable.Vftbl.AbiToProjectionVftablePtr },
            new() { IID = new("2FC57384-A068-44D7-A331-30982FCF7177"), Vtable = CreateEffectVtable() }
        ];

        public ComWrappers.ComInterfaceEntry[] GetExposedInterfaces() => Interfaces;

        private static nint CreateGraphicsVtable()
        {
            var table = (nint*)ComWrappersSupport.AllocateVtableMemory(typeof(InterfaceDetails), 8 * sizeof(nint));
            *(IInspectable.Vftbl*)table = IInspectable.Vftbl.AbiToProjectionVftable;
            table[6] = (nint)(delegate* unmanaged[MemberFunction]<nint, nint*, int>)&GetName;
            table[7] = (nint)(delegate* unmanaged[MemberFunction]<nint, nint, int>)&SetName;
            return (nint)table;
        }

        private static nint CreateEffectVtable()
        {
            var table = (nint*)ComWrappersSupport.AllocateVtableMemory(typeof(InterfaceDetails), 9 * sizeof(nint));
            *(IUnknownVftbl*)table = IUnknownVftbl.AbiToProjectionVftbl;
            table[3] = (nint)(delegate* unmanaged[MemberFunction]<nint, Guid*, int>)&GetEffectId;
            table[4] = (nint)(delegate* unmanaged[MemberFunction]<nint, char*, uint*, uint*, int>)&GetMapping;
            table[5] = (nint)(delegate* unmanaged[MemberFunction]<nint, uint*, int>)&GetPropertyCount;
            table[6] = (nint)(delegate* unmanaged[MemberFunction]<nint, uint, nint*, int>)&GetProperty;
            table[7] = (nint)(delegate* unmanaged[MemberFunction]<nint, uint, nint*, int>)&GetSource;
            table[8] = (nint)(delegate* unmanaged[MemberFunction]<nint, uint*, int>)&GetSourceCount;
            return (nint)table;
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvMemberFunction)])]
        private static int GetName(nint self, nint* name)
        {
            *name = 0;
            try { *name = MarshalString.FromManaged(ComWrappersSupport.FindObject<GlassBlurEffect>(self).Name); }
            catch (Exception ex) { return ex.HResult; }
            return 0;
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvMemberFunction)])]
        private static int SetName(nint self, nint name)
        {
            try { ComWrappersSupport.FindObject<GlassBlurEffect>(self).Name = MarshalString.FromAbi(name); }
            catch (Exception ex) { return ex.HResult; }
            return 0;
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvMemberFunction)])]
        private static int GetEffectId(nint self, Guid* effect)
        {
            *effect = GaussianBlur;
            return 0;
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvMemberFunction)])]
        private static int GetMapping(nint self, char* name, uint* index, uint* mapping)
        {
            *index = *mapping = 0;
            if (name == null || !MemoryMarshal.CreateReadOnlySpanFromNullTerminated(name)
                .SequenceEqual("BlurAmount")) return InvalidArgument;
            *mapping = 1; // GRAPHICS_EFFECT_PROPERTY_MAPPING_DIRECT
            return 0;
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvMemberFunction)])]
        private static int GetPropertyCount(nint self, uint* count)
        {
            *count = 3;
            return 0;
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvMemberFunction)])]
        private static int GetProperty(nint self, uint index, nint* value)
        {
            *value = 0;
            if (index >= 3) return InvalidArgument;
            try
            {
                object property = index switch
                {
                    0 => PropertyValue.CreateSingle(0f),
                    1 => PropertyValue.CreateUInt32(1), // balanced optimization
                    _ => PropertyValue.CreateUInt32(1) // hard border
                };
                nint boxed = MarshalInspectable<object>.FromManaged(property);
                try { return Marshal.QueryInterface(boxed, in PropertyValueInterface, out *value); }
                finally { Marshal.Release(boxed); }
            }
            catch (Exception ex) { return ex.HResult; }
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvMemberFunction)])]
        private static int GetSource(nint self, uint index, nint* source)
        {
            *source = 0;
            if (index != 0) return InvalidArgument;
            try
            {
                *source = MarshalInterface<IGraphicsEffectSource>.FromManaged(
                    ComWrappersSupport.FindObject<GlassBlurEffect>(self).Source);
                return 0;
            }
            catch (Exception ex) { return ex.HResult; }
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvMemberFunction)])]
        private static int GetSourceCount(nint self, uint* count)
        {
            *count = 1;
            return 0;
        }
    }
}
